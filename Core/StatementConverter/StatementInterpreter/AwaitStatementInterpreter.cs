﻿using HotReloading.Core.Statements;
using HotReloading.Syntax.Statements;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace StatementConverter.StatementInterpreter
{
    internal class AwaitStatementInterpreter : IStatementInterpreter
    {
        private readonly StatementInterpreterHandler statementInterpreterHandler;
        private AwaitExpressionSyntax awaitExpressionSyntax;

        public AwaitStatementInterpreter(StatementInterpreterHandler statementInterpreterHandler, AwaitExpressionSyntax awaitExpressionSyntax)
        {
            this.statementInterpreterHandler = statementInterpreterHandler;
            this.awaitExpressionSyntax = awaitExpressionSyntax;
        }

        public IStatementCSharpSyntax GetStatement()
        {
            var awaitStatement = new AwaitStatement();
            awaitStatement.Statement = statementInterpreterHandler.GetStatement(awaitExpressionSyntax.Expression);
            return awaitStatement;
        }
    }
}