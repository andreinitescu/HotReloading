﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HotReloading.BuildTask.Extensions;
using HotReloading.Core;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace HotReloading.BuildTask
{
    public class HotReloadingBuildTask : Task
    {
        private string ctorParametersName = "hotReloading_Ctor_Parameters";
        private FieldReference ctorParameters;

        public HotReloadingBuildTask()
        {
            Logger = new Logger(Log);
        }

        public HotReloadingBuildTask(ILogger logger)
        {
            Logger = logger;
        }

        public ILogger Logger { get; }

        [Required] public string AssemblyFile { get; set; }

        [Required] public string ProjectDirectory { get; set; }
        [Required]
        public string References { get; set; }

        public bool AllowOverride { get; set; }
        public bool DebugSymbols { get; set; }
        public string DebugType { get; set; }

        public override bool Execute()
        {
            var assemblyPath = Path.Combine(ProjectDirectory, AssemblyFile);
            InjectCode(assemblyPath, assemblyPath);

            Logger.LogMessage("Injection done");
            return true;
        }

        public void InjectCode(string assemblyPath, string outputAssemblyPath)
        {
            var debug = DebugSymbols || !string.IsNullOrEmpty(DebugType) && DebugType.ToLowerInvariant() != "none";

            var assemblyReferenceResolver = new AssemblyReferenceResolver();

            if (!string.IsNullOrEmpty(References))
            {
                var paths = References.Replace("//", "/").Split(';').Distinct();
                foreach (var p in paths)
                {
                    var searchpath = Path.GetDirectoryName(p);
                    //Logger.LogMessage($"Adding searchpath {searchpath}");
                    assemblyReferenceResolver.AddSearchDirectory(searchpath);
                }
            }

            var ad = AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters
            {
                ReadWrite = true,
                ReadSymbols = true,
                AssemblyResolver = assemblyReferenceResolver
            });

            var md = ad.MainModule;

            var test1 = md.AssemblyReferences;

            var types = md.Types.Where(x => x.BaseType != null).ToList();

            var typesWithCorrectOrder = new List<TypeDefinition>();

            foreach(var type in types)
            {
                if (type.BaseType is TypeDefinition baseTypeDeginition)
                {
                    var baseTypes = GetBaseTypesWithCorrectOrder(baseTypeDeginition);

                    foreach(var baseType in baseTypes)
                    {
                        if (!typesWithCorrectOrder.Contains(baseType))
                        {
                            typesWithCorrectOrder.Add(baseType);
                        }
                    }
                    typesWithCorrectOrder.Add(type);
                }
                else
                {
                    if(!typesWithCorrectOrder.Contains(type))
                    {
                        typesWithCorrectOrder.Add(type);
                    }
                }
            }

            var iInstanceClassType = md.ImportReference(typeof(IInstanceClass));

            foreach (var type in typesWithCorrectOrder)
            {
                if (type.Name.EndsWith("Delegate", StringComparison.InvariantCulture))
                    continue;
                Logger.LogMessage("Weaving Type: " + type.Name);

                var ctorParametersDefinition = type.CreateField(md, ctorParametersName, md.ImportReference(typeof(ArrayList)));
                ctorParameters = ctorParametersDefinition.GetReference();

                var methods = type.Methods.ToList();

                MethodDefinition getInstanceMethod = null;
                MethodDefinition instanceMethodGetters = null;

                if (type.IsDelegate(md) || type.IsEnum || type.IsValueType)
                    continue;

                var hasImplementedIInstanceClass = type.HasImplementedIInstanceClass(iInstanceClassType, out getInstanceMethod, out instanceMethodGetters);

                ImplementIInstanceClass(md, type, ref getInstanceMethod, ref instanceMethodGetters, hasImplementedIInstanceClass);

                foreach (var method in methods)
                {
                    if (method.CustomAttributes
                            .Any(x => x.AttributeType.Name == "CompilerGeneratedAttribute") ||
                        method.CustomAttributes
                            .Any(x => x.AttributeType.Name == "GeneratedCodeAttribute"))
                        continue;

                    if (method == instanceMethodGetters || method == getInstanceMethod)
                        continue;

                    //Ignore method with ref parameter
                    if (method.Parameters.Any(x => x.ParameterType is ByReferenceType))
                        continue;

                    WrapMethod(md, type, method, getInstanceMethod.GetReference(type, md));

                    if (method.IsVirtual)
                    {
                        var baseMethod = GetBaseMethod(type, method, md);

                        if (baseMethod != null)
                        {
                            var hotReloadingBaseMethod = CreateBaseCallMethod(md, baseMethod);

                            type.Methods.Add(hotReloadingBaseMethod);
                        }
                    }
                }

                if (!type.IsAbstract && !type.IsSealed && AllowOverride)
                {
                    AddOverrideMethod(type, md, getInstanceMethod.GetReference(type, md), methods);
                }
            }

            if (assemblyPath == outputAssemblyPath)
            {
                ad.Write(new WriterParameters
                {
                    WriteSymbols = debug
                });
            }
            else
            {
                ad.Write(outputAssemblyPath, new WriterParameters
                {
                    WriteSymbols = debug
                });
            }

            ad.Dispose();
        }

        private MethodReference GetBaseMethod(TypeDefinition type, MethodDefinition method, ModuleDefinition md)
        {
            if (type.BaseType == null)
                return null;
            var baseTypeDefinition = type.BaseType.Resolve();

            foreach(var baseMethod in baseTypeDefinition.Methods)
            {
                if (baseMethod.IsEqual(method))
                    return baseMethod.GetBaseReference(type, type.BaseType, md);
            }

            return GetBaseMethod(baseTypeDefinition, method, md);
        }

        private List<TypeDefinition> GetBaseTypesWithCorrectOrder(TypeDefinition type)
        {
            var retVal = new List<TypeDefinition>();

            if(type.BaseType is TypeDefinition baseTypeDefinition)
            {
                var baseTypes = GetBaseTypesWithCorrectOrder(baseTypeDefinition);

                foreach(var baseType in baseTypes)
                {
                    if (!retVal.Contains(baseType))
                    {
                        retVal.Add(baseType);
                    }
                }
            }

            retVal.Add(type);

            return retVal;
        }

        private void AddOverrideMethod(TypeDefinition type, 
            ModuleDefinition md, 
            MethodReference getInstanceMethod, 
            List<MethodDefinition> existingMethods)
        {
            if (type.BaseType == null)
                return;

            var overridableMethods = GetOverridableMethods(type, md);

            var baseMethodCalls = new List<MethodReference>();

            foreach(var overridableMethod in overridableMethods)
            {
                if (existingMethods.Any(x => x.FullName == overridableMethod.Method.FullName))
                    continue;
                var baseMethod = overridableMethod.BaseMethodReference;
                //Ignore method with ref parameter
                if (baseMethod.Parameters.Any(x => x.ParameterType is ByReferenceType))
                    continue;
                baseMethodCalls.Add(baseMethod);

                if (!type.Methods.Any(x => x.IsEqual(overridableMethod.Method)))
                {
                    var method = overridableMethod.Method;

                    var returnType = method.ReturnType;

                    var composer = new InstructionComposer(md);

                    composer.LoadArg_0();

                    foreach(var parameter in method.Parameters)
                    {
                        composer.LoadArg(parameter);
                    }

                    composer.BaseCall(baseMethod);

                    if (overridableMethod.Method.ReturnType.FullName != "System.Void")
                    {
                        var returnVariable = new VariableDefinition(returnType);

                        method.Body.Variables.Add(returnVariable);

                        composer.Store(returnVariable);
                        composer.Load(returnVariable);
                    }

                    composer.Return();

                    foreach(var instruction in composer.Instructions)
                    {
                        method.Body.GetILProcessor().Append(instruction);
                    }

                    WrapMethod(md, type, method, getInstanceMethod);

                    method.DeclaringType = type;
                    type.Methods.Add(method);
                }
            }

            foreach (var baseMethod in baseMethodCalls)
            {
                //Ignore optional
                if (baseMethod.Parameters.Any(x => x.IsOptional))
                {
                    continue;
                }
                var hotReloadingBaseMethod = CreateBaseCallMethod(md, baseMethod);

                type.Methods.Add(hotReloadingBaseMethod);
            }
        }

        private static MethodDefinition CreateBaseCallMethod(ModuleDefinition md, MethodReference baseMethod)
        {
            var methodKey = Core.Helper.GetMethodKey(baseMethod.Name, baseMethod.Parameters.Select(x => x.ParameterType.FullName).ToArray());
            var methodName = "HotReloadingBase_" + baseMethod.Name;
            var hotReloadingBaseMethod = new MethodDefinition(methodName, MethodAttributes.Private | MethodAttributes.HideBySig, md.ImportReference(typeof(void)));

            foreach (var parameter in baseMethod.GenericParameters)
            {
                hotReloadingBaseMethod.GenericParameters.Add(parameter);
            }

            hotReloadingBaseMethod.ReturnType = baseMethod.ReturnType;
            foreach (var parameter in baseMethod.Parameters)
            {
                TypeReference parameterType = parameter.ParameterType;
                hotReloadingBaseMethod.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameterType)
                {
                    IsIn = parameter.IsIn,
                    IsOut = parameter.IsOut,
                    IsOptional = parameter.IsOptional
                });
            }

            var retVar = new VariableDefinition(md.ImportReference(typeof(object)));
            var baseCallComposer = new InstructionComposer(md)
                .LoadArg_0();

            foreach (var parameter in hotReloadingBaseMethod.Parameters)
            {
                baseCallComposer.LoadArg(parameter);
            }

            baseCallComposer.BaseCall(baseMethod);

            if (hotReloadingBaseMethod.ReturnType.FullName != "System.Void")
            {
                var returnVariable = new VariableDefinition(hotReloadingBaseMethod.ReturnType);

                hotReloadingBaseMethod.Body.Variables.Add(returnVariable);

                baseCallComposer.Store(returnVariable);
                baseCallComposer.Load(returnVariable);
            }

            baseCallComposer.Return();

            foreach (var instruction in baseCallComposer.Instructions)
            {
                hotReloadingBaseMethod.Body.GetILProcessor().Append(instruction);
            }

            return hotReloadingBaseMethod;
        }

        public class OverridableMethod
        {
            public MethodDefinition Method;
            public MethodReference BaseMethodReference;
        }
        private IEnumerable<OverridableMethod> GetOverridableMethods(TypeDefinition type, ModuleDefinition md)
        {
            if (type.BaseType == null)
                return null;

            var retVal = new List<OverridableMethod>();

            var baseTypeDefinition = type.BaseType.Resolve();
            IEnumerable<MethodDefinition> sealedMethods = new List<MethodDefinition>();
            if (!baseTypeDefinition.Name.EndsWith("Delegate", StringComparison.InvariantCulture))
            {

                //Ignore non virtual, finalized, special name, private and internal
                var methods = baseTypeDefinition.Methods.Where(x => x.IsVirtual &&
                                                                    !x.IsSpecialName &&
                                                                    x.Name != "Finalize" &&
                                                                    (x.IsPublic ||
                                                                    x.IsFamily ||
                                                                    x.IsFamilyOrAssembly));


                sealedMethods = methods.Where(x => x.IsFinal);
                foreach (var method in methods)
                {
                    if (method.IsFinal)
                        continue;

                    //Ignore method with export method
                    if (method.CustomAttributes.Any(x => x.AttributeType.Name == "ExportAttribute"))
                        continue;

                    //Ignore Xamarin.iOS protocols method
                    if (baseTypeDefinition.Interfaces.Where(x => x.InterfaceType.Name.EndsWith("Delegate", StringComparison.InvariantCulture))
                    .Select(x => x.InterfaceType).OfType<TypeDefinition>()
                    .SelectMany(x => x.Methods).Any(x => x.IsEqual(method)))
                        continue;

                    retVal.Add(CopyMethod(method, type, type.BaseType, md));
                }
            }

            var baseOverriableMethods = GetOverridableMethods(baseTypeDefinition, md);
            if (baseOverriableMethods == null)
                return retVal;

            foreach(var method in baseOverriableMethods)
            {
                if (retVal.Any(x => x.Method.IsEqual(method.Method)))
                    continue;
                if (sealedMethods.Any(x => x.IsEqual(method.Method)))
                    continue;
                retVal.Add(CopyMethod(method.Method, type, type.BaseType, md));
            }

            return retVal;
        }

        private OverridableMethod CopyMethod(MethodDefinition overridableMethod, TypeReference targetType, TypeReference sourceType, ModuleDefinition md)
        {
            Logger.LogMessage("\tOverriding: " + overridableMethod.FullName);
            var attributes = overridableMethod.Attributes & ~MethodAttributes.NewSlot | MethodAttributes.ReuseSlot;
            var method = new MethodDefinition(overridableMethod.Name, attributes, md.ImportReference(typeof(void)));
            method.ImplAttributes = overridableMethod.ImplAttributes;
            method.SemanticsAttributes = overridableMethod.SemanticsAttributes;
            method.DeclaringType = targetType.Resolve();

            foreach (var genericParameter in overridableMethod.GenericParameters)
            {
                method.GenericParameters.Add(new GenericParameter(targetType.GetUniqueGenericParameterName(), method));
            }

            TypeReference returnType = overridableMethod.ReturnType.CopyType(targetType, sourceType, md, method);

            method.ReturnType = returnType;

            foreach (var parameter in overridableMethod.Parameters)
            {
                TypeReference parameterType = parameter.ParameterType.CopyType(targetType, sourceType, md, method);
                method.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameterType)
                {
                    IsOptional = parameter.IsOptional,
                    IsIn = parameter.IsIn,
                    IsOut = parameter.IsOut
                });
            }
            var baseMethodReference = overridableMethod.GetBaseReference(targetType, sourceType, md);  
             
            return new OverridableMethod
            {
                Method = method,
                BaseMethodReference = baseMethodReference
            };
        }

        private static void ImplementIInstanceClass(ModuleDefinition md, TypeDefinition type, ref MethodDefinition getInstanceMethod, ref MethodDefinition instanceMethodGetters, bool hasImplementedIInstanceClass)
        {
            if (!type.IsAbstract && !hasImplementedIInstanceClass)
            {
                type.Interfaces.Add(new InterfaceImplementation(md.ImportReference(typeof(IInstanceClass))));
                PropertyDefinition instanceMethods = CreateInstanceMethodsProperty(md, type, out instanceMethodGetters);
                getInstanceMethod = CreateGetInstanceMethod(md, instanceMethods.GetMethod.GetReference(type, md),type,hasImplementedIInstanceClass);
            }
        }

        private static PropertyDefinition CreateInstanceMethodsProperty(ModuleDefinition md, TypeDefinition type, out MethodDefinition instanceMethodGetters)
        {
            var instaceMethodType = md.ImportReference(typeof(Dictionary<string, Delegate>));
            var field = type.CreateField(md, "instanceMethods", instaceMethodType);
            var fieldReference = field.GetReference();

            var returnComposer = new InstructionComposer(md)
                .Load_1()
                .Return();

            var getFieldComposer = new InstructionComposer(md)
                .LoadArg_0()
                .Load(fieldReference)
                .Store_1()
                .MoveTo(returnComposer.Instructions.First());

            var initializeField = new InstructionComposer(md)
                .NoOperation()
                .LoadArg_0()
                .LoadArg_0()
                .StaticCall(new Method
                {
                    ParentType = typeof(Runtime),
                    MethodName = nameof(Runtime.GetInitialInstanceMethods),
                    ParameterSignature = new[] { typeof(IInstanceClass) }
                })
                .Store(fieldReference)
                .NoOperation();

            var getterComposer = new InstructionComposer(md);
            getterComposer.LoadArg_0()
                .Load(fieldReference)
                .LoadNull()
                .CompareEqual()
                .Store_0()
                .Load_0()
                .MoveToWhenFalse(getFieldComposer.Instructions.First())
                .Append(initializeField)
                .Append(getFieldComposer)
                .Append(returnComposer);

            instanceMethodGetters = type.CreateGetter(md, "InstanceMethods", instaceMethodType, getterComposer.Instructions, vir: true);

            var boolVariable = new VariableDefinition(md.ImportReference(typeof(bool)));
            instanceMethodGetters.Body.Variables.Add(boolVariable);

            var delegateVariable = new VariableDefinition(md.ImportReference(typeof(Delegate)));
            instanceMethodGetters.Body.Variables.Add(delegateVariable);

            return type.CreateProperty("InstanceMethods", instaceMethodType, instanceMethodGetters, null);
        }

        private void WrapMethod(ModuleDefinition md, TypeDefinition type, MethodDefinition method, MethodReference getInstanceMethod)
        {
            Logger.LogMessage("\tWeaving Method " + method.Name);

            var methodKeyVariable = new VariableDefinition(md.ImportReference(typeof(string)));
            method.Body.Variables.Add(methodKeyVariable);

            var delegateVariable = new VariableDefinition(md.ImportReference(typeof(Delegate)));
            method.Body.Variables.Add(delegateVariable);

            var boolVariable = new VariableDefinition(md.ImportReference(typeof(bool)));
            method.Body.Variables.Add(boolVariable);

            var instructions = method.Body.Instructions;
            var ilprocessor = method.Body.GetILProcessor();
            Instruction loadInstructionForReturn = null;
            if (method.ReturnType.FullName != "System.Void" && instructions.Count > 1)
            {
                var secondLastInstruction =  instructions.ElementAt(instructions.Count - 2);

                if (secondLastInstruction.IsLoadInstruction())
                    loadInstructionForReturn = secondLastInstruction;
            }

            var retInstruction = instructions.Last();

            List<Instruction> constructorInitialCode = new List<Instruction>();
            if(method.IsConstructor)
            {
                foreach(var i in method.Body.Instructions)
                {
                    if (i.OpCode != OpCodes.Call)
                        constructorInitialCode.Add(i);
                    else
                    {
                        constructorInitialCode.Add(i);
                        break;
                    }
                }
            }

            var oldInstruction = method.Body.Instructions.Where(x => x != retInstruction &&
                                                                    x != loadInstructionForReturn &&
                                                                    !constructorInitialCode.Contains(x)).ToList();

            var lastInstruction = retInstruction;

            method.Body.Instructions.Clear();

            if (loadInstructionForReturn != null)
            {
                method.Body.Instructions.Add(loadInstructionForReturn);
                lastInstruction = loadInstructionForReturn;
            }

            method.Body.Instructions.Add(retInstruction);

            foreach (var instruction in oldInstruction) ilprocessor.InsertBefore(lastInstruction, instruction);

            method.Body.InitLocals = true;
            var firstInstruction = method.Body.Instructions.First();

            var parameters = method.Parameters.ToArray();

            var composer = new InstructionComposer(md);

            if(method.IsStatic)
                ComposeStaticMethodInstructions(type, method, delegateVariable, boolVariable, methodKeyVariable, firstInstruction, parameters, composer);
            else
                ComposeInstanceMethodInstructions(type, method, delegateVariable, boolVariable, methodKeyVariable, firstInstruction, parameters, composer, getInstanceMethod, md);

            if (method.ReturnType.FullName == "System.Void") composer.Pop();
            else if (method.ReturnType.IsValueType)
            {
                composer.Unbox_Any(method.ReturnType);
            }
            else
            {
                composer.Cast(method.ReturnType);
            }

            if (loadInstructionForReturn != null)
            {
                Instruction storeInstructionForReturn = loadInstructionForReturn.GetStoreInstruction();
                composer.Append(storeInstructionForReturn);
            }

            composer.MoveTo(lastInstruction);

            foreach (var instruction in composer.Instructions) ilprocessor.InsertBefore(firstInstruction, instruction);

            foreach(var instruction in constructorInitialCode)
            {
                ilprocessor.InsertBefore(composer.Instructions[0], instruction);
            }
        }

        private void ComposeInstanceMethodInstructions(TypeDefinition type, MethodDefinition method, VariableDefinition delegateVariable, VariableDefinition boolVariable, VariableDefinition methodKeyVariable, Instruction firstInstruction, ParameterDefinition[] parameters, InstructionComposer composer, MethodReference getInstanceMethod, ModuleDefinition md)
        {
            if(method.IsConstructor)
            {
                var arrayListConstructor = typeof(ArrayList).GetConstructor(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null, new Type[] { }, null);
                var arrayListConstructorReference = md.ImportReference(arrayListConstructor);
                composer
                    .LoadArg_0()
                    .NewObject(arrayListConstructorReference)
                    .Store(ctorParameters);

                foreach(var parameter in method.Parameters)
                {
                    composer.LoadArg_0()
                        .Load(ctorParameters)
                        .LoadArg(parameter)
                        .InstanceCall(new Method
                        {
                            ParentType = typeof(ArrayList),
                            MethodName = "Add",
                            ParameterSignature = new Type[] { typeof(Object)}
                        })
                        .Pop();
                }
            }
            composer
            .Load(method.Name)
                .LoadArray(parameters.Length, typeof(string), parameters.Select(x => x.ParameterType.FullName).ToArray())
                .StaticCall(new Method
                {
                    ParentType = typeof(Core.Helper),
                    MethodName = nameof(Core.Helper.GetMethodKey),
                    ParameterSignature = new[] { typeof(string), typeof(string[]) }
                }).
                Store(methodKeyVariable)
                .LoadArg_0()
                .Load(methodKeyVariable)
                .InstanceCall(getInstanceMethod)
                .Store(delegateVariable)
                .Load(delegateVariable)
                .IsNotNull()
                .Store(boolVariable)
                .Load(boolVariable)
                .MoveToWhenFalse(firstInstruction)
                .NoOperation()
                .Load(delegateVariable)
                .LoadArray(parameters, true)
                .InstanceCall(new Method
                {
                    ParentType = typeof(Delegate),
                    MethodName = nameof(Delegate.DynamicInvoke),
                    ParameterSignature = new[] { typeof(object[]) }
                });
        }

        private static void ComposeStaticMethodInstructions(TypeDefinition type, MethodDefinition method, VariableDefinition delegateVariable, VariableDefinition boolVariable, VariableDefinition methodKeyVariable, Instruction firstInstruction, ParameterDefinition[] parameters, InstructionComposer composer)
        {
            composer
            .Load(method.Name)
                .LoadArray(parameters.Length, typeof(string), parameters.Select(x => x.ParameterType.FullName).ToArray())
                .StaticCall(new Method
                {
                    ParentType = typeof(Core.Helper),
                    MethodName = nameof(Core.Helper.GetMethodKey),
                    ParameterSignature = new[] { typeof(string), typeof(string[])}
                }).
                Store(methodKeyVariable)
                .Load(type)
                            .StaticCall(new Method
                            {
                                ParentType = typeof(Type),
                                MethodName = nameof(Type.GetTypeFromHandle),
                                ParameterSignature = new[] { typeof(RuntimeTypeHandle) }
                            })
                            .Load(methodKeyVariable)
                            .StaticCall(new Method
                            {
                                ParentType = typeof(Runtime),
                                MethodName = nameof(Runtime.GetMethodDelegate),
                                ParameterSignature = new[] { typeof(Type), typeof(string) }
                            })
                            .Store(delegateVariable)
                            .Load(delegateVariable)
                            .IsNotNull()
                            .Store(boolVariable)
                            .Load(boolVariable)
                            .MoveToWhenFalse(firstInstruction)
                            .NoOperation()
                            .Load(delegateVariable)
                            .LoadArray(parameters)
                            .InstanceCall(new Method
                            {
                                ParentType = typeof(Delegate),
                                MethodName = nameof(Delegate.DynamicInvoke),
                                ParameterSignature = new[] { typeof(object[]) }
                            });
        }

        private static MethodDefinition CreateGetInstanceMethod(ModuleDefinition md, MethodReference instanceMethodGetter, TypeDefinition type, bool hasImplementedIInstanceClass)
        {
            var getInstanceMethod = new MethodDefinition(Constants.GetInstanceMethodName, MethodAttributes.Family,
                md.ImportReference(typeof(Delegate)));

            var methodName = new ParameterDefinition("methodName", ParameterAttributes.None,
                md.ImportReference(typeof(string)));
            getInstanceMethod.Parameters.Add(methodName);

            var boolVariable = new VariableDefinition(md.ImportReference(typeof(bool)));
            getInstanceMethod.Body.Variables.Add(boolVariable);

            var delegateVariable = new VariableDefinition(md.ImportReference(typeof(Delegate)));
            getInstanceMethod.Body.Variables.Add(delegateVariable);

            var composer1 = new InstructionComposer(md)
                .Load(delegateVariable)
                .Return();

            var composer2 = new InstructionComposer(md).LoadNull()
                .Store(delegateVariable)
                .MoveTo(composer1.Instructions.First());

            var composer = new InstructionComposer(md)
                .LoadArg_0()
                .InstanceCall(instanceMethodGetter)
                .Load(methodName)
                .InstanceCall(new Method
                {
                    ParentType = typeof(Dictionary<string, Delegate>),
                    MethodName = "ContainsKey",
                    ParameterSignature = new[] {typeof(Dictionary<string, Delegate>).GetGenericArguments()[0]}
                })
                .Store(boolVariable)
                .Load(boolVariable)
                .MoveToWhenFalse(composer2.Instructions.First())
                .LoadArg_0()
                .InstanceCall(instanceMethodGetter)
                .Load(methodName)
                .InstanceCall(new Method
                {
                    ParentType = typeof(Dictionary<string, Delegate>),
                    MethodName = "get_Item",
                    ParameterSignature = new[] {typeof(Dictionary<string, Delegate>).GetGenericArguments()[0]}
                })
                .Store(delegateVariable)
                .MoveTo(composer1.Instructions.First())
                .Append(composer2)
                .Append(composer1);

            var ilProcessor = getInstanceMethod.Body.GetILProcessor();

            foreach (var instruction in composer.Instructions) ilProcessor.Append(instruction);

            if (!type.IsAbstract & !hasImplementedIInstanceClass)
                type.Methods.Add(getInstanceMethod);

            return getInstanceMethod;
        }
    }
}