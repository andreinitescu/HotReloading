﻿using System;
using FluentAssertions;
using HotReloading.Core;
using NUnit.Framework;
using StatementConverter.Extensions;

namespace StatementConverter.Test.UnitTests.TypeExtensions
{
    [TestFixture]
    public class TypeExtensionTest
    {
        [Test]
        public void TestSimpleType()
        {
            var typeSymbol = Helper.GetTypeSymbol("TypeTestClass", "SimpleType");

            var classType2 = (HrType)typeSymbol.GetHrType();

            classType2.AssemblyQualifiedName.Should().Be("StatementConverter.Test.InstanceTestClass, StatementConverter.Test");
        }

        [Test]
        public void TestGenericTypeWithOneArgument()
        {
            var typeSymbol = Helper.GetTypeSymbol("TypeTestClass", "GenericTypeWithOneArgument");

            var classType2 = (HrType)typeSymbol.GetHrType();
            var test = classType2.AssemblyQualifiedName;

            test.Should().Be("StatementConverter.Test.GenericClass`1[[System.Int32, System.Private.CoreLib]], StatementConverter.Test");
        }

        [Test]
        public void TestGenericTypeWithTwoArgument()
        {
            var typeSymbol = Helper.GetTypeSymbol("TypeTestClass", "GenericTypeWithTwoArgument");

            var classType2 = (HrType)typeSymbol.GetHrType();

            classType2.AssemblyQualifiedName.Should().Be("StatementConverter.Test.GenericClass`2[[System.Int32, System.Private.CoreLib], [System.Int32, System.Private.CoreLib]], StatementConverter.Test");
        }

        [Test]
        public void TestOneDArray()
        {
            var typeSymbol = Helper.GetTypeSymbol("TypeTestClass", "OneDArray");

            var classType1 = (HrType)typeSymbol.GetHrType();

            classType1.AssemblyQualifiedName.Should().Be("System.Int32[], System.Private.CoreLib");
        }

        [Test]
        public void TestTwoDArray()
        {
            var typeSymbol = Helper.GetTypeSymbol("TypeTestClass", "TwoDArray");

            var classType1 = (HrType)typeSymbol.GetHrType();

            classType1.AssemblyQualifiedName.Should().Be("System.Int32[][], System.Private.CoreLib");
        }
    }
}
