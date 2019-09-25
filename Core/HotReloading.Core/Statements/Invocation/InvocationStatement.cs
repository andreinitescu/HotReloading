﻿using System.Collections.Generic;

namespace HotReloading.Core.Statements
{
    public class InvocationStatement : IStatementCSharpSyntax
    {
        public InvocationStatement()
        {
            Arguments = new List<IStatementCSharpSyntax>();
        }

        public MethodMemberStatement Method { get; set; }
        public List<IStatementCSharpSyntax> Arguments { get; set; }
        public BaseHrType[] ParametersSignature { get; set; }
    }
}