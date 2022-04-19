﻿// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;

namespace ICSharpCode.Decompiler.ILAst {
	abstract class YieldReturnDecompiler {
		// For a description on the code generated by the C# compiler for yield return:
		// http://csharpindepth.com/Articles/Chapter6/IteratorBlockImplementation.aspx

		// The idea here is:
		// - Figure out whether the current method is instanciating an enumerator
		// - Figure out which of the fields is the state field
		// - Construct an exception table based on states. This allows us to determine, for each state, what the parent try block is.

		// See http://community.sharpdevelop.net/blogs/danielgrunwald/archive/2011/03/06/ilspy-yield-return.aspx
		// for a description of this step.

		protected readonly DecompilerContext context;
		protected readonly AutoPropertyProvider autoPropertyProvider;
		protected TypeDef enumeratorType;
		protected MethodDef enumeratorCtor;
		protected MethodDef disposeMethod;
		protected FieldDef stateField;
		protected FieldDef currentField;
		protected FieldToVariableMap variableMap;
		protected List<ILNode> newBody;
		protected MethodDef iteratorMoveNextMethod;

		// See Microsoft.CodeAnalysis.CSharp.MethodToStateMachineRewriter.cachedThis for info on why and when it's cached
		protected ILVariable cachedThisVar;

		public abstract string CompilerName { get; }

		protected YieldReturnDecompiler(DecompilerContext context, AutoPropertyProvider autoPropertyProvider) {
			this.context = context;
			this.autoPropertyProvider = autoPropertyProvider;
			variableMap = context.VariableMap;
		}

		static YieldReturnDecompiler TryCreate(DecompilerContext context, ILBlock method, AutoPropertyProvider autoPropertyProvider) =>
			MicrosoftYieldReturnDecompiler.TryCreateCore(context, method, autoPropertyProvider) ??
			MonoYieldReturnDecompiler.TryCreateCore(context, method, autoPropertyProvider) ??
			VisualBasic11YieldReturnDecompiler.TryCreateCore(context, method, autoPropertyProvider);

		#region Run() method
		public static void Run(DecompilerContext context, ILBlock method, AutoPropertyProvider autoPropertyProvider, ref StateMachineKind stateMachineKind, ref MethodDef inlinedMethod, ref string compilerName, List<ILNode> list_ILNode, Func<ILBlock, ILInlining> getILInlining, List<ILExpression> listExpr, List<ILBlock> listBlock, Dictionary<ILLabel, int> labelRefCount) {
			if (!context.Settings.YieldReturn)
				return; // abort if enumerator decompilation is disabled
			var yrd = TryCreate(context, method, autoPropertyProvider);
			if (yrd == null)
				return;
			try {
				yrd.Run();
				Debug.Assert(yrd.iteratorMoveNextMethod != null);
			}
			catch (SymbolicAnalysisFailedException) {
				return;
			}
			context.CurrentMethodIsYieldReturn = true;
			method.Body.Clear();
			method.EntryGoto = null;
			method.Body.AddRange(yrd.newBody);
			stateMachineKind = StateMachineKind.IteratorMethod;
			inlinedMethod = yrd.iteratorMoveNextMethod;
			compilerName = yrd.CompilerName;
			BaseMethodWrapperFixer.FixBaseCalls(context.CurrentMethod.DeclaringType, method, listExpr);

			// Repeat the inlining/copy propagation optimization because the conversion of field access
			// to local variables can open up additional inlining possibilities.
			var inlining = getILInlining(method);
			inlining.InlineAllVariables();
			inlining.CopyPropagation(list_ILNode);
			ILAstOptimizer.RemoveRedundantCode(context, method, listExpr, listBlock, labelRefCount);
		}

		void Run() {
			AnalyzeCtor();
			AnalyzeCurrentProperty();
			ResolveIEnumerableIEnumeratorFieldMapping();
			AnalyzeDispose();
			AnalyzeMoveNext();
			TranslateFieldsToLocalAccess();
		}
		#endregion

		public static bool IsCompilerGeneratorEnumerator(TypeDef type) {
			if (!(type.DeclaringType != null && type.IsCompilerGenerated()))
				return false;
			foreach (var i in type.Interfaces) {
				if (i.Interface == null)
					continue;
				if (i.Interface.Name == "IEnumerator" && i.Interface.Namespace == "System.Collections")
					return true;
			}
			return false;
		}

		protected static FieldDef GetFieldDefinition(IField field) => DnlibExtensions.ResolveFieldWithinSameModule(field);
		protected static MethodDef GetMethodDefinition(IMethod method) => DnlibExtensions.ResolveMethodWithinSameModule(method);

		protected virtual void AnalyzeCtor() { }

		/// <summary>
		/// Creates ILAst for the specified method, optimized up to before the 'YieldReturn' step.
		/// </summary>
		protected ILBlock CreateILAst(MethodDef method) {
			if (method == null || !method.HasBody)
				throw new SymbolicAnalysisFailedException();

			ILBlock ilMethod = new ILBlock(CodeBracesRangeFlags.MethodBraces);

			var astBuilder = context.Cache.GetILAstBuilder();
			try {
				ilMethod.Body = astBuilder.Build(method, true, context);
			}
			finally {
				context.Cache.Return(astBuilder);
			}

			var optimizer = this.context.Cache.GetILAstOptimizer();
			try {
				optimizer.Optimize(context, ilMethod, autoPropertyProvider, out _, out _, out _, ILAstOptimizationStep.YieldReturn);
			}
			finally {
				this.context.Cache.Return(optimizer);
			}

			return ilMethod;
		}

		protected bool InitializeFieldToParameterMap(ILBlock method, ILVariable enumVar, ref int i) =>
			InitializeFieldToParameterMap(method, enumVar, ref i, method.Body.Count);

		protected bool InitializeFieldToParameterMap(ILBlock method, ILVariable enumVar, ref int i, int end) {
			for (; i < end; i++) {
				// stfld(..., ldloc(var_1), ldloc(parameter))
				IField storedField;
				ILExpression ldloc, loadParameter;
				if (!method.Body[i].Match(ILCode.Stfld, out storedField, out ldloc, out loadParameter))
					break;
				ILVariable loadedVar, loadedArg;
				if (!ldloc.Match(ILCode.Ldloc, out loadedVar))
					return false;
				ITypeDefOrRef type;
				if (!loadParameter.Match(ILCode.Ldloc, out loadedArg) &&
					!(loadParameter.Match(ILCode.Ldobj, out type, out ldloc) && ldloc.Match(ILCode.Ldloc, out loadedArg))) {
					// VB 11 & 12 calls RuntimeHelpers.GetObjectValue(o)
					IMethod m;
					if (!loadParameter.Match(ILCode.Call, out m, out ldloc) || !ldloc.Match(ILCode.Ldloc, out loadedArg))
						return false;
					if (m.Name != nameGetObjectValue)
						return false;
					if (m.DeclaringType.FullName != "System.Runtime.CompilerServices.RuntimeHelpers")
						return false;
				}
				if (loadedVar != enumVar)
					return false;
				var fd = GetFieldDefinition(storedField);
				if (fd == null || !loadedArg.IsParameter)
					return false;
				variableMap.SetParameter(fd, loadedArg);
			}
			return true;
		}
		static readonly UTF8String nameGetObjectValue = new UTF8String("GetObjectValue");

		#region Figure out what the 'current' field is (analysis of get_Current())
		/// <summary>
		/// Looks at the enumerator's get_Current method and figures out which of the fields holds the current value.
		/// </summary>
		void AnalyzeCurrentProperty() {
			foreach (var getCurrentMethod in MethodUtils.GetMethod_get_Current(enumeratorType)) {
				ILBlock method = CreateILAst(getCurrentMethod);
				if (method.Body.Count == 1) {
					// release builds directly return the current field
					ILExpression retExpr;
					IField field;
					ILExpression ldFromObj;
					if (method.Body[0].Match(ILCode.Ret, out retExpr) &&
						retExpr.Match(ILCode.Ldfld, out field, out ldFromObj) &&
						ldFromObj.MatchThis()) {
						currentField = GetFieldDefinition(field);
					}
				}
				else if (method.Body.Count == 2) {
					ILVariable v, v2;
					ILExpression stExpr;
					IField field;
					ILExpression ldFromObj;
					ILExpression retExpr;
					if (method.Body[0].Match(ILCode.Stloc, out v, out stExpr) &&
						stExpr.Match(ILCode.Ldfld, out field, out ldFromObj) &&
						ldFromObj.MatchThis() &&
						method.Body[1].Match(ILCode.Ret, out retExpr) &&
						retExpr.Match(ILCode.Ldloc, out v2) &&
						v == v2) {
						currentField = GetFieldDefinition(field);
					}
				}
				if (currentField != null)
					break;
			}
			if (currentField == null)
				throw new SymbolicAnalysisFailedException();
		}
		#endregion

		#region Figure out the mapping of IEnumerable fields to IEnumerator fields  (analysis of GetEnumerator())
		void ResolveIEnumerableIEnumeratorFieldMapping() {
			foreach (var getEnumeratorMethod in MethodUtils.GetMethod_GetEnumerator(enumeratorType)) {
				bool found = false;
				ILBlock method = CreateILAst(getEnumeratorMethod);
				foreach (ILNode node in method.Body) {
					IField stField;
					ILExpression stToObj;
					ILExpression stExpr;
					IField ldField;
					IMethod m;
					ILExpression ldFromObj;
					if (node.Match(ILCode.Stfld, out stField, out stToObj, out stExpr) &&
						(stExpr.Match(ILCode.Ldfld, out ldField, out ldFromObj) ||
						// VB 11 & 12 calls this method
						(stExpr.Match(ILCode.Call, out m, out stExpr) && m.Name == nameGetObjectValue &&
						m.DeclaringType.FullName == "System.Runtime.CompilerServices.RuntimeHelpers") &&
						stExpr.Match(ILCode.Ldfld, out ldField, out ldFromObj)) &&
						ldFromObj.MatchThis()) {
						found = true;
						FieldDef storedField = GetFieldDefinition(stField);
						FieldDef loadedField = GetFieldDefinition(ldField);
						if (storedField != null && loadedField != null) {
							ILVariable mappedParameter;
							if (variableMap.TryGetParameter(loadedField, out mappedParameter))
								variableMap.SetParameter(storedField, mappedParameter);
						}
					}
				}
				if (found)
					break;
			}
		}
		#endregion

		protected abstract void AnalyzeDispose();
		protected abstract void AnalyzeMoveNext();

		#region TranslateFieldsToLocalAccess
		void TranslateFieldsToLocalAccess() => TranslateFieldsToLocalAccess(newBody, variableMap, cachedThisVar, context.CalculateILSpans, true);
		internal static void TranslateFieldsToLocalAccess(List<ILNode> newBody, FieldToVariableMap variableMap, ILVariable cachedThisField, bool calculateILSpans, bool fixLocals) {
			variableMap.Version++;
			ILVariable realThisParameter = null;
			if (cachedThisField != null) {
				foreach (var kv in variableMap.GetParameters()) {
					if (kv.Value.OriginalParameter?.IsHiddenThisParameter == true) {
						realThisParameter = kv.Value;
						break;
					}
				}
				if (realThisParameter == null)
					throw new SymbolicAnalysisFailedException();
			}
			List<ILExpression> listExpr = null;
			foreach (ILNode node in newBody) {
				foreach (ILExpression expr in node.GetSelfAndChildrenRecursive(listExpr ?? (listExpr = new List<ILExpression>()))) {
					FieldDef field;
					ILVariable parameter, local;
					switch (expr.Code) {
					case ILCode.Ldfld:
						if (expr.Arguments[0].MatchThis() && (field = GetFieldDefinition(expr.Operand as IField)) != null) {
							if (variableMap.TryGetParameter(field, out parameter))
								expr.Operand = parameter;
							else if (!fixLocals) {
								if (!variableMap.TryGetLocal(field, out local))
									break;
								expr.Operand = local;
							}
							else
								expr.Operand = variableMap.GetOrCreateLocal(field);
							expr.Code = ILCode.Ldloc;
							if (calculateILSpans)
								expr.ILSpans.AddRange(expr.Arguments[0].GetSelfAndChildrenRecursiveILSpans());
							expr.Arguments.Clear();
						}
						break;
					case ILCode.Stfld:
						if (expr.Arguments[0].MatchThis() && (field = GetFieldDefinition(expr.Operand as IField)) != null) {
							if (variableMap.TryGetParameter(field, out parameter))
								expr.Operand = parameter;
							else if (!fixLocals) {
								if (!variableMap.TryGetLocal(field, out local))
									break;
								expr.Operand = local;
							}
							else
								expr.Operand = variableMap.GetOrCreateLocal(field);
							expr.Code = ILCode.Stloc;
							if (calculateILSpans)
								expr.ILSpans.AddRange(expr.Arguments[0].GetSelfAndChildrenRecursiveILSpans());
							expr.Arguments.RemoveAt(0);
						}
						break;
					case ILCode.Ldflda:
						if (expr.Arguments[0].MatchThis() && (field = GetFieldDefinition(expr.Operand as IField)) != null) {
							if (variableMap.TryGetParameter(field, out parameter))
								expr.Operand = parameter;
							else if (!fixLocals) {
								if (!variableMap.TryGetLocal(field, out local))
									break;
								expr.Operand = local;
							}
							else
								expr.Operand = variableMap.GetOrCreateLocal(field);
							expr.Code = ILCode.Ldloca;
							if (calculateILSpans)
								expr.ILSpans.AddRange(expr.Arguments[0].GetSelfAndChildrenRecursiveILSpans());
							expr.Arguments.Clear();
						}
						break;
					case ILCode.Ldloc:
						if (expr.Operand == cachedThisField)
							expr.Operand = realThisParameter;
						break;
					}
				}
			}
			if (calculateILSpans) {
				foreach (var kv in variableMap.GetParameters())
					kv.Value.HoistedField = kv.Key;
			}
		}
		#endregion
	}

	sealed class FieldToVariableMap {
		public int Version;
		readonly Dictionary<FieldDef, ILVariable> paramDict;
		readonly DefaultDictionary<FieldDef, ILVariable> localDict;

		public FieldToVariableMap() {
			paramDict = new Dictionary<FieldDef, ILVariable>();
			localDict = new DefaultDictionary<FieldDef, ILVariable>(f => new ILVariable(string.IsNullOrEmpty(f.Name) ? "_f_" + f.Rid.ToString("X") : f.Name.String) { Type = f.FieldType, HoistedField = f });
		}

		public Dictionary<FieldDef, ILVariable> GetParameters() => paramDict;
		public bool TryGetParameter(FieldDef field, out ILVariable parameter) => paramDict.TryGetValue(field, out parameter);
		public void SetParameter(FieldDef field, ILVariable parameter) => paramDict[field] = parameter;
		public bool TryGetLocal(FieldDef field, out ILVariable local) => localDict.TryGetValue(field, out local);
		public ILVariable GetOrCreateLocal(FieldDef field) => localDict[field];
	}
}
