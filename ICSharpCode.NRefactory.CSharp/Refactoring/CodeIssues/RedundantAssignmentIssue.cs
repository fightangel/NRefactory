﻿// 
// RedundantAssignmentIssue.cs
// 
// Author:
//      Mansheng Yang <lightyang0@gmail.com>
// 
// Copyright (c) 2012 Mansheng Yang <lightyang0@gmail.com>
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System.Collections.Generic;
using System.Linq;
using ICSharpCode.NRefactory.CSharp.Resolver;
using ICSharpCode.NRefactory.Semantics;

namespace ICSharpCode.NRefactory.CSharp.Refactoring
{
	[IssueDescription ("Redundant assignment",
					   Description = "Value assigned to a variable or parameter is not used in all execution path.",
					   Category = IssueCategories.CodeQualityIssues,
					   Severity = Severity.Warning,
					   IssueMarker = IssueMarker.GrayOut)]
	public class RedundantAssignmentIssue : ICodeIssueProvider
	{
		public IEnumerable<CodeIssue> GetIssues (BaseRefactoringContext context)
		{
			var unit = context.RootNode as CompilationUnit;
			if (unit == null)
				return Enumerable.Empty<CodeIssue> ();
			return new GatherVisitor (context, unit).GetIssues ();
		}

		class GatherVisitor : GatherVisitorBase
		{
			static FindReferences refFinder = new FindReferences ();

			CompilationUnit unit;

			public GatherVisitor (BaseRefactoringContext ctx, CompilationUnit unit)
				: base (ctx)
			{
				this.unit = unit;
			}

			public override void VisitParameterDeclaration (ParameterDeclaration parameterDeclaration)
			{
				base.VisitParameterDeclaration (parameterDeclaration);
				if (parameterDeclaration.ParameterModifier == ParameterModifier.Out ||
					parameterDeclaration.ParameterModifier == ParameterModifier.Ref)
					return;

				var resolveResult = ctx.Resolve (parameterDeclaration) as LocalResolveResult;
				BlockStatement rootStatement = null;
				if (parameterDeclaration.Parent is MethodDeclaration) {
					rootStatement = ((MethodDeclaration)parameterDeclaration.Parent).Body;
				} else if (parameterDeclaration.Parent is AnonymousMethodExpression) {
					rootStatement = ((AnonymousMethodExpression)parameterDeclaration.Parent).Body;
				} else if (parameterDeclaration.Parent is LambdaExpression) {
					rootStatement = ((LambdaExpression)parameterDeclaration.Parent).Body as BlockStatement;
				}
				CollectIssues (parameterDeclaration, rootStatement, resolveResult);
			}

			public override void VisitVariableInitializer (VariableInitializer variableInitializer)
			{
				base.VisitVariableInitializer (variableInitializer);

				var resolveResult = ctx.Resolve (variableInitializer) as LocalResolveResult;
				CollectIssues (variableInitializer, variableInitializer.GetParent<BlockStatement> (), resolveResult);
			}

			void CollectIssues (AstNode variableDecl, BlockStatement rootStatement, LocalResolveResult resolveResult)
			{
				if (rootStatement == null || resolveResult == null)
					return;
				var references = new HashSet<AstNode> ();
				var refStatements = new HashSet<Statement> ();
				var usedInLambda = false;
				refFinder.FindLocalReferences (resolveResult.Variable, ctx.ParsedFile, unit, ctx.Compilation,
					(astNode, rr) => {
						if (usedInLambda || astNode == variableDecl)
							return;

						var parent = astNode.Parent;
						while (!(parent == null || parent is Statement || parent is LambdaExpression))
							parent = parent.Parent;
						if (parent == null)
							return;

						var statement = parent as Statement;
						if (statement != null) {
							references.Add (astNode);
							refStatements.Add (statement);
						}

						while (parent != null && parent != rootStatement) {
							if (parent is LambdaExpression || parent is AnonymousMethodExpression) {
								usedInLambda = true;
								break;
							}
							parent = parent.Parent;
						}
					}, ctx.CancellationToken);

				// stop analyzing if the variable is used in any lambda expression or anonymous method
				if (usedInLambda)
					return;

				var startNode = VariableReferenceGraphBuilder.Build (rootStatement, references, refStatements, ctx);
				var variableInitializer = variableDecl as VariableInitializer;
				if (variableInitializer != null && !variableInitializer.Initializer.IsNull)
					startNode.References.Insert (0, variableInitializer);

				ProcessNodes (startNode);
			}

			void AddIssue (AstNode node)
			{
				if (node.Parent is DirectionExpression && ((DirectionExpression)node.Parent).Expression == node)
					return;

				// TODO: correct marking
				AddIssue (node, ctx.TranslateString ("Remove redundant assignment"),
					script => {
						if (node is VariableInitializer) {
							var replacement = (VariableInitializer)node.Clone ();
							replacement.Initializer = Expression.Null;
							script.Replace (node, replacement);
							return;
						}

						var assignmentExpr = (AssignmentExpression)node.Parent;
						if (assignmentExpr.Parent is ExpressionStatement) {
							script.Remove (assignmentExpr.Parent);
						} else {
							script.Replace (assignmentExpr, assignmentExpr.Right.Clone ());
						}
					});
			}

			static bool IsAssignment (AstNode node)
			{
				if (node is VariableInitializer)
					return true;

				var assignmentExpr = node.Parent as AssignmentExpression;
				if (assignmentExpr != null)
					return assignmentExpr.Left == node && assignmentExpr.Operator == AssignmentOperatorType.Assign;

				var direction = node.Parent as DirectionExpression;
				if (direction != null)
					return direction.FieldDirection == FieldDirection.Out && direction.Expression == node;

				return false;
			}

			enum NodeState
			{
				None,
				UsageReachable,
				UsageUnreachable,
				Processing,
			}

			void ProcessNodes (VariableReferenceNode startNode)
			{
				var nodeStates = new Dictionary<VariableReferenceNode, NodeState> ();
				var assignments = new List<VariableReferenceNode> ();

				// dfs to preprocess all nodes and find nodes which end with assignment
				var stack = new Stack<VariableReferenceNode> ();
				stack.Push (startNode);
				while (stack.Count > 0) {
					var node = stack.Pop ();
					if (node.References.Count > 0) {
						nodeStates [node] = IsAssignment (node.References [0]) ?
							NodeState.UsageUnreachable : NodeState.UsageReachable;
					} else {
						nodeStates [node] = NodeState.None;
					}

					var assignmentIndexes = new List<int> ();
					for (int i = 0; i < node.References.Count; i++) {
						if (IsAssignment (node.References [i]))
							assignmentIndexes.Add (i);
					}

					for (int i = 0; i < assignmentIndexes.Count - 1; i++) {
						var index1 = assignmentIndexes [i];
						var index2 = assignmentIndexes [i + 1];
						if (index1 + 1 == index2)
							AddIssue (node.References [index1]);
					}
					if (assignmentIndexes.Count > 0 &&
						assignmentIndexes [assignmentIndexes.Count - 1] == node.References.Count - 1)
						assignments.Add (node);

					foreach (var nextNode in node.NextNodes) {
						if (!nodeStates.ContainsKey (nextNode))
							stack.Push (nextNode);
					}
				}

				foreach (var node in assignments)
					ProcessNode (node, true, nodeStates);
			}

			void ProcessNode (VariableReferenceNode node, bool addIssue,
				IDictionary<VariableReferenceNode, NodeState> nodeStates)
			{
				if (nodeStates [node] == NodeState.None)
					nodeStates [node] = NodeState.Processing;

				bool? reachable = false;
				foreach (var nextNode in node.NextNodes) {
					if (nodeStates [nextNode] == NodeState.None)
						ProcessNode (nextNode, false, nodeStates);

					if (nodeStates [nextNode] == NodeState.UsageReachable) {
						reachable = true;
						break;
					}
					if (nodeStates [nextNode] == NodeState.Processing)
						reachable = null;
				}

				// not possible to reach any usage via NextNodes
				if (addIssue && reachable == false)
					AddIssue (node.References [node.References.Count - 1]);

				if (nodeStates [node] != NodeState.Processing) 
					return;

				switch (reachable) {
					case null:
						nodeStates [node] = NodeState.None;
						break;
					case true:
						nodeStates [node] = NodeState.UsageReachable;
						break;
					case false:
						nodeStates [node] = NodeState.UsageUnreachable;
						break;
				}
			}
		}
	}
}
