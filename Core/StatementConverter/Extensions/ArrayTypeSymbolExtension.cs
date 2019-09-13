﻿using HotReloading.Core;
using Microsoft.CodeAnalysis;

namespace StatementConverter.Extensions
{
    public static class ArrayTypeSymbolExtension
    {
        public static Type GetClassType(this IArrayTypeSymbol type)
        {
            Type classType;

            if (type.ElementType is IArrayTypeSymbol)
            {
                classType = GetClassType((IArrayTypeSymbol)type.ElementType);
            }
            else
                classType = type.ElementType.GetClassType();

            var arrowBraket = "[";

            for (var i = 1; i < type.Rank; i++)
                arrowBraket += ",";

            arrowBraket += "]";

            return new Type
            {
                Name = classType.Name + arrowBraket,
                AssemblyName = classType.AssemblyName
            };
        }
    }
}