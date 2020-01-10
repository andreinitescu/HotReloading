﻿namespace HotReloading.Syntax.Statements
{
    public class DelegateIdentifierStatement : IStatementCSharpSyntax
    {
        public IStatementCSharpSyntax Target { get; set; }
        public BaseHrType Type { get; set; }
    }
}