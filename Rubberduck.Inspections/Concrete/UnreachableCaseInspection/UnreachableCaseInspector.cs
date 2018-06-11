﻿using Antlr4.Runtime;
using Rubberduck.Parsing;
using Rubberduck.Parsing.Grammar;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Rubberduck.Inspections.Concrete.UnreachableCaseInspection
{
    public interface IUnreachableCaseInspector
    {
        void InspectForUnreachableCases();
        List<ParserRuleContext> UnreachableCases { get; }
        List<ParserRuleContext> MismatchTypeCases { get; }
        List<ParserRuleContext> UnreachableCaseElseCases { get; }
    }

    public interface IUnreachableCaseInspectorTest
    {
        string SelectExpressionTypeName { get; }
    }

    public class UnreachableCaseInspector : IUnreachableCaseInspector, IUnreachableCaseInspectorTest
    {
        private readonly IEnumerable<VBAParser.CaseClauseContext> _caseClauses;
        private readonly ParserRuleContext _caseElseContext;
        private readonly IParseTreeValueFactory _valueFactory;

        private Dictionary<string, IExpressionFilter> _filters;

        public UnreachableCaseInspector(VBAParser.SelectCaseStmtContext selectCaseContext, IParseTreeVisitorResults inspValues, IParseTreeValueFactory valueFactory)
        {
            _valueFactory = valueFactory;
            _caseClauses = selectCaseContext.caseClause();
            _caseElseContext = selectCaseContext.caseElseClause();
            _filters = new Dictionary<string, IExpressionFilter>();
            ParseTreeValueResults = inspValues;
            SetSelectExpressionTypeName(selectCaseContext as ParserRuleContext, inspValues);
        }

        public List<ParserRuleContext> UnreachableCases { set; get; } = new List<ParserRuleContext>();

        public List<ParserRuleContext> MismatchTypeCases { set; get; } = new List<ParserRuleContext>();

        public List<ParserRuleContext> UnreachableCaseElseCases { set; get; } = new List<ParserRuleContext>();

        public string SelectExpressionTypeName { private set; get; }

        public void InspectForUnreachableCases()
        {
            if (!InspectableTypes.Contains(SelectExpressionTypeName))
            {
                return;
            }

            var expressionFilter = ExpressionFilterFactory.Create(SelectExpressionTypeName);
            _filters.Add("SelectCase", expressionFilter);

            foreach (var caseClause in _caseClauses)
            {
                var rangeClauseExpressions = (from range in caseClause.rangeClause()
                                   select GetRangeClauseExpression(range)).ToList();

                rangeClauseExpressions.ForEach(expr => expressionFilter.AddExpression(expr));

                if (rangeClauseExpressions.All(expr => expr.IsMismatch))
                {
                    MismatchTypeCases.Add(caseClause);
                }
                else if (rangeClauseExpressions.All(expr => expr.IsUnreachable || expr.IsMismatch))
                {
                    UnreachableCases.Add(caseClause);
                }
            }

            if (_caseElseContext != null && expressionFilter.FiltersAllValues)
            {
                UnreachableCaseElseCases.Add(_caseElseContext);
            }
        }

        private IParseTreeVisitorResults ParseTreeValueResults { set; get; }

        private static List<string> InspectableTypes = new List<string>()
        {
            Tokens.Byte,
            Tokens.Integer,
            Tokens.Int,
            Tokens.Long,
            Tokens.LongLong,
            Tokens.Single,
            Tokens.Double,
            Tokens.Decimal,
            Tokens.Currency,
            Tokens.Boolean,
            Tokens.String
        };

        private void SetSelectExpressionTypeName(ParserRuleContext context, IParseTreeVisitorResults inspValues)
        {
            var selectStmt = (VBAParser.SelectCaseStmtContext)context;
            if (TryDetectTypeHint(selectStmt.selectExpression().GetText(), out string typeName)
                && InspectableTypes.Contains(typeName))
            {
                SelectExpressionTypeName = typeName;
            }
            else if (inspValues.TryGetValue(selectStmt.selectExpression(), out IParseTreeValue result)
                && InspectableTypes.Contains(result.TypeName))
            {
                SelectExpressionTypeName = result.TypeName;
            }
            else
            {
                SelectExpressionTypeName = DeriveTypeFromCaseClauses(inspValues, selectStmt);
            }
        }

        private string DeriveTypeFromCaseClauses(IParseTreeVisitorResults inspValues, VBAParser.SelectCaseStmtContext selectStmt)
        {
            var caseClauseTypeNames = new List<string>();
            foreach (var caseClause in selectStmt.caseClause())
            {
                foreach (var range in caseClause.rangeClause())
                {
                    if (TryDetectTypeHint(range.GetText(), out string hintTypeName))
                    {
                        caseClauseTypeNames.Add(hintTypeName);
                    }
                    else
                    {
                        var typeNames = from context in range.children
                                where context is ParserRuleContext 
                                    && ParseTreeValueVisitor.IsResultContext(context)
                                select inspValues.GetTypeName(context as ParserRuleContext);

                        caseClauseTypeNames.AddRange(typeNames);
                        caseClauseTypeNames.RemoveAll(tp => !InspectableTypes.Contains(tp));
                    }
                }
            }

            if (TryGetSelectExpressionTypeNameFromTypes(caseClauseTypeNames, out string evalTypeName))
            {
                return evalTypeName;
            }
            return string.Empty;
        }

        private static bool TryGetSelectExpressionTypeNameFromTypes(IEnumerable<string> typeNames, out string typeName)
        {
            typeName = string.Empty;
            if(!typeNames.Any()) { return false; }

            var typeList = typeNames.ToList();

            //If everything is declared as a Variant, we do not attempt to inspect the selectStatement
            if (typeList.All(tn => new List<string>() { Tokens.Variant }.Contains(tn)))
            {
                return false;
            }

            //If all match, the typeName is easy...This is the only way to return "String".
            if (typeList.All(tn => new List<string>() { typeList.First() }.Contains(tn)))
            {
                typeName = typeList.First();
                return true;
            }
            //Integral numbers will be evaluated using Long
            if (typeList.All(tn => new List<string>() { Tokens.Long, Tokens.Integer, Tokens.Byte }.Contains(tn)))
            {
                typeName = Tokens.Long;
                return true;
            }

            //Mix of Integertypes and rational number types will be evaluated using Double or Currency
            if (typeList.All(tn => new List<string>() { Tokens.Long, Tokens.Integer, Tokens.Byte, Tokens.Single, Tokens.Double, Tokens.Currency }.Contains(tn)))
            {
                typeName = typeList.Any(tk => tk.Equals(Tokens.Currency)) ? Tokens.Currency : Tokens.Double;
                return true;
            }
            return false;
        }

        private static bool TryDetectTypeHint(string content, out string typeName)
        {
            typeName = string.Empty;
            if (SymbolList.TypeHintToTypeName.Keys.Any(th => content.EndsWith(th)))
            {
                var lastChar = content.Substring(content.Length - 1);
                typeName = SymbolList.TypeHintToTypeName[lastChar];
                return true;
            }
            return false;
        }

        private IRangeClauseExpression GetRangeClauseExpression(VBAParser.RangeClauseContext rangeClause)
        {
            var resultContexts = from ctxt in rangeClause.children
                             where ctxt is ParserRuleContext && ParseTreeValueVisitor.IsResultContext(ctxt)
                             select ctxt as ParserRuleContext;

            if (!resultContexts.Any())
            {
                throw new NullReferenceException("No result context(s) found");
            }

            var clauseValue = ParseTreeValueResults.GetValue(resultContexts.First());

            if (rangeClause.TO() != null)
            {
                var rangeStartValue = ParseTreeValueResults.GetValue(rangeClause.GetChild<VBAParser.SelectStartValueContext>());
                var rangeEndValue = ParseTreeValueResults.GetValue(rangeClause.GetChild<VBAParser.SelectEndValueContext>());
                return new RangeValuesExpression(rangeStartValue, rangeEndValue);
            }
            else if (rangeClause.IS() != null)
            {
                var isClauseSymbol = rangeClause.GetChild<VBAParser.ComparisonOperatorContext>().GetText();
                return new IsClauseExpression(clauseValue, isClauseSymbol);
            }
            else if (rangeClause.children.Any(ch => ParseTreeValueVisitor.IsLogicalContext(ch)))
            {
                if (clauseValue.ParsesToConstantValue)
                {
                    return new ValueExpression(clauseValue);
                }
                else
                {
                    var symbol = GetLogicSymbol(resultContexts.First() as VBAParser.ExpressionContext);

                    Debug.Assert(!symbol.Equals(string.Empty), "Unhandled ExpressionContext detected");
                    if (symbol == string.Empty) { return null; }

                    var resultContext = resultContexts.First();
                    if (resultContext is VBAParser.LogicalNotOpContext)
                    {
                        return new UnaryExpression(clauseValue, symbol);
                    }
                    else if (resultContext is VBAParser.RelationalOpContext
                            || resultContext is VBAParser.LogicalEqvOpContext
                            || resultContext is VBAParser.LogicalImpOpContext)
                    {
                        (IParseTreeValue lhs, IParseTreeValue rhs) = CreateOperandPair(clauseValue, symbol, _valueFactory);
                        return new BinaryExpression(lhs, rhs, symbol);
                    }
                }
            }
            else
            {
                return new ValueExpression(clauseValue);
            }
            return null;
        }

        private static (IParseTreeValue lhs, IParseTreeValue rhs)
            CreateOperandPair(IParseTreeValue value, string opSymbol, IParseTreeValueFactory factory)
        {
            var operands = value.ValueText.Split(new string[] { opSymbol }, StringSplitOptions.None);
            if (operands.Count() == 2)
            {
                var lhs = factory.Create(operands[0].Trim());
                var rhs = factory.Create(operands[1].Trim());
                return (lhs, rhs);
            }

            if (operands.Count() == 1)
            {
                var lhs = new ParseTreeValue(operands[0].Trim());
                return (lhs, null);
            }
            return (null, null);
        }

        private static string GetLogicSymbol<T>(T context) where T : VBAParser.ExpressionContext
        {
            switch (context)
            {
                case VBAParser.RelationalOpContext ctxt:
                    var terminalNode = ctxt.EQ() ?? ctxt.GEQ() ?? ctxt.GT() ?? ctxt.LEQ()
                        ?? ctxt.LIKE() ?? ctxt.LT() ?? ctxt.NEQ();
                    return terminalNode.GetText();
                case VBAParser.LogicalXorOpContext _:
                    return context.GetToken(VBAParser.XOR, 0).GetText();
                case VBAParser.LogicalAndOpContext _:
                    return context.GetToken(VBAParser.AND, 0).GetText();
                case VBAParser.LogicalOrOpContext _:
                    return context.GetToken(VBAParser.OR, 0).GetText();
                case VBAParser.LogicalEqvOpContext _:
                    return context.GetToken(VBAParser.EQV, 0).GetText();
                case VBAParser.LogicalImpOpContext _:
                    return context.GetToken(VBAParser.IMP, 0).GetText();
                case VBAParser.LogicalNotOpContext _:
                    return context.GetToken(VBAParser.NOT, 0).GetText();
                default:
                    return string.Empty;
            }
        }
    }
}
