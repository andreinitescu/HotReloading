﻿namespace HotReloading.Core.Statements
{
    public class SwitchCase
    {
        public IStatementCSharpSyntax Body { get; set; }
        public IStatementCSharpSyntax[] Tests { get; set; }
    }
}
