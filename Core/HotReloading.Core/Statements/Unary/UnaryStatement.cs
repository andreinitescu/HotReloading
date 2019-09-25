﻿using System;
namespace HotReloading.Core.Statements
{
    public class UnaryStatement : IStatementCSharpSyntax
    {
        public IStatementCSharpSyntax Operand { get; set; }
        public UnaryOperand Operator { get; set; }
    }
}
