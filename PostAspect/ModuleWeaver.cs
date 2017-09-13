/*
 * Copyright (c) 2017 VMware, Inc. All Rights Reserved.
 * 
 * Licensed under the MIT License, Version 2.0 (the "License"); 
 * You may not use this file except in compliance with the License. 
 * You may obtain a copy of the License at 
 * 
 *     https://opensource.org/licenses/MIT
 *  
 * Unless required by applicable law or agreed to in writing, software 
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Rocks;
using Mono.Cecil.Cil;
using PostAspect;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Xml;
using System.Threading.Tasks;

/// <summary>
/// Module Weaver providing fody entry point for assembly rewrite
/// </summary>
public class ModuleWeaver
{
    /// <summary>
    /// Object Type Name
    /// </summary>
    private const string ObjectTypeName = "System.Object";

    /// <summary>
    /// Logger delegate
    /// </summary>
    public Action<string> LogInfo { get; set; }

    /// <summary>
    /// Module Definition
    /// </summary>
    public ModuleDefinition ModuleDefinition { get; set; }

    /// <summary>
    /// Type Custom Attributes
    /// </summary>
    public Dictionary<string, List<CustomAttribute>> TypeCustomAttributes =
        new Dictionary<string, List<CustomAttribute>>();

    /// <summary>
    /// Counter for unique name generator for all type definition (Methods, Fields, etc)
    /// </summary>
    public static long Counter = 0;

    /// <summary>
    /// Module type systems
    /// </summary>
    TypeSystem TypeSystem;

    /// <summary>
    /// Aspect type to determine whether to weave methods
    /// </summary>
    TypeReference aspectType;

    /// <summary>
    /// Constructor for attribute to apply to generated methods. Since there's no source code, we don't want the debugger to stop there.
    /// </summary>
    MethodReference debuggerStepThroughAttributeCtor;

    /// <summary>
    /// Initialize an instance of <see cref="ModuleWeaver"/>
    /// </summary>
    public ModuleWeaver()
    {
        LogInfo = m => { };
    }

    /// <summary>
    /// Return true if target type is a subclass of given @classs
    /// </summary>
    /// <param name="target">Type to check if it is a subclass of @class</param>
    /// <param name="class">SubClass type to match</param>
    /// <returns></returns>
    private bool IsSubClass(TypeReference target, TypeReference @class)
    {
        if(target.FullName == @class.FullName)
        {
            return true;
        }

        var baseType = target.Resolve().BaseType;

        while(baseType != null && baseType.FullName != ObjectTypeName)
        {
            if(baseType.FullName == @class.FullName)
            {
                return true;
            }

            baseType = baseType.Resolve().BaseType;
        }

        return false;
    }

    /// <summary>
    /// Get Custom Attribute for given type
    /// </summary>
    /// <param name="type">Type</param>
    /// <param name="attrType">Attribute Type</param>
    /// <returns><see cref="Collection{CustomAttributea}"/></returns>
    private List<CustomAttribute> GetCustomAttributes(TypeDefinition type, TypeReference attrType)
    {
        var key = type.FullName;

        List<CustomAttribute> attributes;

        if(TypeCustomAttributes.TryGetValue(key, out attributes))
        {
            return attributes;
        }

        var list = new List<CustomAttribute>();

        foreach(var attr in type.CustomAttributes)
        {
            if(IsSubClass(attr.AttributeType, attrType))
            {
                list.Add(attr);
            }
        }

        var baseType = type?.BaseType?.Resolve();

        while (baseType != null && baseType.FullName != ObjectTypeName)
        {
            list.AddRange(GetCustomAttributes(baseType, attrType));

            baseType = baseType?.BaseType?.Resolve();
        }

        TypeCustomAttributes[key] = list;
        
        return list;
    }

    /// <summary>
    /// Execute logic to rewrite assembly
    /// </summary>
    public void Execute()
    {
        debuggerStepThroughAttributeCtor = ModuleDefinition.Import(typeof(DebuggerStepThroughAttribute).GetConstructor(Type.EmptyTypes));
        aspectType = ModuleDefinition.Import(typeof(BaseAspect));
        TypeSystem = ModuleDefinition.TypeSystem;

        var asmAttrs = ModuleDefinition.Assembly.CustomAttributes.Where(c => IsSubClass(c.AttributeType, aspectType));

        var types = ModuleDefinition.GetAllTypes()
            .Where(x => !IsSubClass(x, aspectType) && x.FullName != aspectType.FullName &&
                x.FullName != "<Module>")
            .Select(x =>
            new
            {
                Type = x.Resolve(),
                TypeAttributes = x.CustomAttributes.Union(x.BaseType != null ? x.BaseType.Resolve().CustomAttributes : new Mono.Collections.Generic.Collection<CustomAttribute>()).Where(c => IsSubClass(c.AttributeType.Resolve(), aspectType)),
                TypeMethodAttributes = x.GetMethods().SelectMany(m => m.CustomAttributes.Where(c => IsSubClass(c.AttributeType, aspectType))).ToList()
            })
            .Where(x => x.TypeAttributes.Any() || x.TypeMethodAttributes.Any()).ToList();

        var typeImp = new TypeDefinition("PostAspect.Generated", "TypeImplementation", TypeAttributes.Public | TypeAttributes.Class, TypeSystem.Object);

        var cCtor = AddStaticConstructor(typeImp);
        cCtor.CustomAttributes.Add(new CustomAttribute(debuggerStepThroughAttributeCtor));

        var cctorIL = cCtor.Body.GetILProcessor();

        var definedAspects = new Dictionary<string, FieldDefinition>();

        ModuleDefinition.Types.Add(typeImp);

        foreach (var t in types)
        {
            var type = t.Type;
            var name = type.FullName;
            var typeAttributes = t.TypeAttributes;
            var methods = type.GetMethods().ToList();

            //Assuming non value Type class constructor code size greater than 8 indicates explicit default constructor
            var ctors = type.GetConstructors().Where(x => x.DeclaringType.IsValueType || x.Body.CodeSize > 8).ToList();
            var fields = type.Fields;

            foreach(var field in fields)
            {
                if((field.Attributes & FieldAttributes.InitOnly) == FieldAttributes.InitOnly)
                {
                    field.Attributes &= ~FieldAttributes.InitOnly;
                }
            }

            //Cache instance of all aspects in preparation for use in methods
            foreach(var attr in UniqueAttributes(typeAttributes.Union(t.TypeMethodAttributes)))
            {
                var attrType = attr.AttributeType;
                var attrTypeName = attrType.FullName;
                if (!definedAspects.ContainsKey(attrTypeName))
                {
                    var aspectField = new FieldDefinition("_aspect_" + attrTypeName, FieldAttributes.Public | FieldAttributes.Static, ModuleDefinition.Import(typeof(BaseAspect)));

                    cctorIL.Emit(OpCodes.Newobj, ModuleDefinition.Import(attrType.Resolve().GetConstructors().FirstOrDefault(x => x.Parameters.Count == 0)));
                    cctorIL.Emit(OpCodes.Stsfld, aspectField);

                    typeImp.Fields.Add(aspectField);

                    definedAspects[attrTypeName] = aspectField;
                }
            }

            foreach (var ctor in ctors)
            {
                var attrs = UniqueAttributes(ctor.CustomAttributes.Where(c => IsSubClass(c.AttributeType, aspectType)).Union(typeAttributes).Union(asmAttrs));
                if (!attrs.Any())
                {
                    continue;
                }
                
                UpdateMethodIL(ctor, typeImp, cctorIL, attrs, definedAspects, type);
            }

            foreach (var method in methods)
            {
                var attrs = UniqueAttributes(method.CustomAttributes.Where(c => IsSubClass(c.AttributeType, aspectType)).Union(typeAttributes).Union(asmAttrs));
                //Skip abstract and partial method definition
                if (!method.HasBody || !attrs.Any())
                {
                    continue;
                }
                
                UpdateMethodIL(method, typeImp, cctorIL, attrs, definedAspects, type);
            }
        }
        cctorIL.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Add static constructor to the given type
    /// </summary>
    /// <param name="type">Type</param>
    /// <returns>MethodDefinition</returns>
    private MethodDefinition AddStaticConstructor(TypeDefinition type)
    {
        var method = new MethodDefinition(".cctor", MethodAttributes.Static | MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, TypeSystem.Void);
        var objectConstructor = ModuleDefinition.Import(TypeSystem.Object.Resolve().GetConstructors().First());
        var body = method.Body;
        var processor = method.Body.GetILProcessor();

        body.InitLocals = true;
        body.OptimizeMacros();

        type.Methods.Add(method);
        return method;
    }

    /// <summary>
    /// Create a clone of given method and add to the specified type
    /// </summary>
    /// <param name="method">Method</param>
    /// <param name="type">Type</param>
    /// <returns>MethodDefinition</returns>
    private MethodDefinition CloneMethod(MethodDefinition method, TypeDefinition type)
    {
        var methodAttribute = MethodAttributes.Private;
        if (method.IsStatic)
        {
            methodAttribute |= MethodAttributes.Static;
        }

        var meth = new MethodDefinition(method.Name + "_☈_", methodAttribute, method.ReturnType);
        meth.CallingConvention = method.CallingConvention;
        meth.ImplAttributes = method.ImplAttributes;
        meth.IsGetter = method.IsGetter;
        meth.IsSetter = method.IsSetter;
        meth.HasThis = method.HasThis;
        
        var body = meth.Body;
        var oldBody = method.Body;
        var il = body.GetILProcessor();

        var instructions = oldBody.Instructions;
        var count = instructions.Count;
        var startIndex = 0;

        if (method.HasGenericParameters)
        {
            foreach (var generic in method.GenericParameters)
            {
                var genericParameter = new GenericParameter(generic.Name, meth);
                generic.Constraints.ToList().ForEach(x => genericParameter.Constraints.Add(x));
                generic.CustomAttributes.ToList().ForEach(x => genericParameter.CustomAttributes.Add(x));
                genericParameter.Attributes = generic.Attributes;
                generic.GenericParameters.ToList().ForEach(x => genericParameter.GenericParameters.Add(new GenericParameter(x.Name, meth)));

                meth.GenericParameters.Add(genericParameter);
            }
        }

        foreach (var param in method.Parameters)
        {
            var paramAttributes = param.Attributes;
            paramAttributes &= ~ParameterAttributes.HasDefault;
            meth.Parameters.Add(new ParameterDefinition(param.Name, paramAttributes, param.ParameterType));
        }

        foreach (var variable in method.Body.Variables)
        {
            body.Variables.Add(variable);
        }


        if (!method.DeclaringType.IsSequentialLayout && method.DeclaringType.IsClass && method.IsConstructor)
        {
            for (var i = 0; i < count; i++)
            {
                var instruction = instructions[i];
                if (instruction.OpCode == OpCodes.Call && (instruction.Operand as MethodReference).Resolve().IsConstructor)
                {
                    startIndex = i + 1;
                    break;
                }
            }
        }

        instructions = new Mono.Collections.Generic.Collection<Instruction>(instructions.Skip(startIndex).ToList());

        foreach (var instruction in instructions)
        {
            il.Append(instruction);
        }

        foreach(var handler in oldBody.ExceptionHandlers)
        {
            body.ExceptionHandlers.Add(handler);
        }

        body.InitLocals = true;
        body.OptimizeMacros();
        
        return meth;
    }

    /// <summary>
    /// Retrieve attributes for the given method
    /// </summary>
    /// <param name="method">Methodss</param>
    /// <returns>List{Attribute}</returns>
    [DebuggerStepThrough]
    public static IList<Attribute> GetAttributes(System.Reflection.MethodBase method)
    {
        return new ReadOnlyCollection<Attribute>(UniqueAttributes(System.Reflection.CustomAttributeExtensions.GetCustomAttributes(method, true).Union(System.Reflection.CustomAttributeExtensions.GetCustomAttributes(method.DeclaringType, true))).ToList());
    }

    /// <summary>
    /// Retrieve parameter attributes for the given method
    /// </summary>
    /// <param name="method">Methodss</param>
    /// <returns>List{Attribute}</returns>
    [DebuggerStepThrough]
    public static IList<Attribute> GetParameterAttributes(System.Reflection.MethodBase method, int index)
    {
        return new ReadOnlyCollection<Attribute>(System.Reflection.CustomAttributeExtensions.GetCustomAttributes(method.GetParameters()[index], true).ToList());
    }

    /// <summary>
    /// Return default value for given type
    /// </summary>
    /// <typeparam name="T"><see cref="T"/></typeparam>
    /// <returns><see cref="T"/></returns>
    [DebuggerStepThrough]
    public static T GetDefault<T>()
    {
        return default(T);
    }

    /// <summary>
    /// Returns result for current method execution if exists.
    /// </summary>
    /// <typeparam name="T">T</typeparam>
    /// <param name="methodInfo">Aspect Method Info</param>
    /// <returns>T</returns>
    [DebuggerStepThrough]
    public static T GetReturn<T>(AspectMethodInfo methodInfo)
    {
        var returns = methodInfo.Returns;

        if (returns != null)
        {
            return (T)returns;
        }

        return GetDefault<T>();
    }

    /// <summary>
    /// Handle Exception for given task
    /// </summary>
    /// <param name="aspect">Aspect</param>
    /// <param name="methodInfo">Aspect Method</param>
    /// <param name="task">Task</param>
    /// <returns>Task</returns>
    [DebuggerStepThrough]
    public static Task HandleTaskError(BaseAspect aspect, AspectMethodInfo methodInfo, Task task)
    {
        task.ContinueWith((t, state) =>
        {
            HandleTaskError(methodInfo, t, state);
        }, new Tuple<BaseAspect, AspectMethodInfo>(aspect, methodInfo), TaskContinuationOptions.OnlyOnFaulted);

        return task;
    }

    /// <summary>
    /// Handle Success for given task
    /// </summary>
    /// <param name="aspect">Aspect</param>
    /// <param name="methodInfo">Aspect Method</param>
    /// <param name="task">Task</param>
    /// <returns>Task</returns>
    [DebuggerStepThrough]
    public static Task HandleTaskSuccess(BaseAspect aspect, AspectMethodInfo methodInfo, Task task)
    {
        task.ContinueWith((t, state) =>
        {
            var tuple = state as Tuple<BaseAspect, AspectMethodInfo>;
            var aspectInfo = tuple.Item1;
            var info = tuple.Item2;

            info.Returns = t;
            aspectInfo.OnSuccess(info);

        }, new Tuple<BaseAspect, AspectMethodInfo>(aspect, methodInfo), TaskContinuationOptions.None);

        return task;
    }

    /// <summary>
    /// Handle Exit for given task
    /// </summary>
    /// <param name="aspect">Aspect</param>
    /// <param name="methodInfo">Aspect Method</param>
    /// <param name="task">Task</param>
    /// <returns>Task</returns>
    [DebuggerStepThrough]
    public static Task HandleTaskExit(BaseAspect aspect, AspectMethodInfo methodInfo, Task task)
    {
        task.ContinueWith((t, state) =>
        {
            var tuple = state as Tuple<BaseAspect, AspectMethodInfo>;
            var aspectInfo = tuple.Item1;
            var info = tuple.Item2;
            info.Returns = t;
            aspectInfo.OnExit(info);
        }, new Tuple<BaseAspect, AspectMethodInfo>(aspect, methodInfo), TaskContinuationOptions.None);

        return task;
    }

    /// <summary>
    /// Handle Exception for given task
    /// </summary>
    /// <param name="methodInfo">Aspect Method Info</param>
    /// <param name="task">Task</param>
    /// <param name="state">State Object</param>
    private static void HandleTaskError(AspectMethodInfo methodInfo, Task task, object state)
    {
        var tuple = state as Tuple<BaseAspect, AspectMethodInfo>;
        var aspectInfo = tuple.Item1;
        var info = tuple.Item2;

        info.Returns = task;
        aspectInfo.OnError(info);

        if (!methodInfo.ReThrow)
        {
            task.Exception.Handle(ex => true);
        }
    }

    /// <summary>
    /// Update the given method IL to include the interception logic
    /// </summary>
    /// <param name="method">Method</param>
    /// <param name="typeImp">Static Type to add logic for caching method info to</param>
    /// <param name="cctorIL">Static IL Processor</param>
    /// <param name="attrs">Custom Attribute for the given method</param>
    /// <param name="aspects">Collection of aspect discovered for the given method</param>
    /// <param name="type">Original Method Type to use for inteception logic</param>
    private void UpdateMethodIL(MethodDefinition method, TypeDefinition typeImp, ILProcessor cctorIL, IEnumerable<CustomAttribute> attrs, Dictionary<string, FieldDefinition> aspects, TypeDefinition type)
    {
        //Check if aspect ignore
        var ignoreAspect = method.CustomAttributes.Any(x => x.AttributeType.FullName == typeof(AspectIgnoreAttribute).FullName);

        var methAttrs = attrs;

        // No Aspect required
        if (ignoreAspect || !methAttrs.Any())
        {
            return;
        }

        var origMethod = CloneMethod(method, type);

        type.Methods.Add(origMethod);

        var declaringType = method.DeclaringType;
        var returnType = method.ReturnType;
        var isGeneric = declaringType.HasGenericParameters;
        var isStatic = method.IsStatic;
        var parameterOffset = isStatic ? 0 : 1;
        var isTask = returnType.FullName.StartsWith("Task`1") || returnType.FullName == typeof(Task).FullName;

        //Define method info
        var methodField = new FieldDefinition("_meth_" + (++Counter), FieldAttributes.Public | FieldAttributes.Static, ModuleDefinition.Import(typeof(System.Reflection.MethodBase)));

        cctorIL.Emit(OpCodes.Ldtoken, method);
        if (isGeneric)
        {
            cctorIL.Emit(OpCodes.Ldtoken, declaringType);
        }
        cctorIL.Emit(OpCodes.Call, ModuleDefinition.Import(typeof(System.Reflection.MethodBase).GetMethod("GetMethodFromHandle", 
            isGeneric ? new Type[] { typeof(RuntimeMethodHandle), typeof(RuntimeTypeHandle) } : new Type[] { typeof(RuntimeMethodHandle) }
            )));
        cctorIL.Emit(OpCodes.Stsfld, methodField);

        typeImp.Fields.Add(methodField);

        var paramType = ModuleDefinition.Import(typeof(AspectMethodParameterInfo));
        var paramCtor = ModuleDefinition.Import(paramType.Resolve().GetConstructors().First());

        //Define method parameters
        var paramsLocal = new VariableDefinition(ModuleDefinition.Import(typeof(List<AspectMethodParameterInfo>)));
        var paramsField = new FieldDefinition("_meth_param" + (++Counter), FieldAttributes.Public | FieldAttributes.Static, ModuleDefinition.Import(typeof(ReadOnlyCollection<AspectMethodParameterInfo>)));
        var paramAdd = ModuleDefinition.Import(typeof(List<AspectMethodParameterInfo>).GetMethod("Add", new[] { typeof(AspectMethodParameterInfo) }));

        var methodAttributesField = new FieldDefinition("_meth_attr_" + (++Counter), FieldAttributes.Public | FieldAttributes.Static, ModuleDefinition.Import(typeof(IList<Attribute>)));

        cctorIL.Emit(OpCodes.Ldtoken, method);
        if (isGeneric)
        {
            cctorIL.Emit(OpCodes.Ldtoken, declaringType);
        }
        cctorIL.Emit(OpCodes.Call, ModuleDefinition.Import(typeof(System.Reflection.MethodBase).GetMethod("GetMethodFromHandle",
            isGeneric ? new Type[] { typeof(RuntimeMethodHandle), typeof(RuntimeTypeHandle) } : new Type[] { typeof(RuntimeMethodHandle) }
            )));
        cctorIL.Emit(OpCodes.Call, ModuleDefinition.Import(typeof(ModuleWeaver).GetMethod("GetAttributes")));
        cctorIL.Emit(OpCodes.Stsfld, methodAttributesField);
        
        typeImp.Fields.Add(methodAttributesField);
        typeImp.Fields.Add(paramsField);

        // Add params local for method parameter information
        cctorIL.Body.Variables.Add(paramsLocal);

        cctorIL.Emit(OpCodes.Newobj, ModuleDefinition.Import(typeof(List<AspectMethodParameterInfo>).GetConstructor(Type.EmptyTypes)));
        cctorIL.Emit(OpCodes.Stloc, paramsLocal);

        foreach (var param in method.Parameters)
        {
            var paramName = param.Name;
            var paramLocal = new VariableDefinition(paramType);
            cctorIL.Body.Variables.Add(paramLocal);

            cctorIL.Emit(OpCodes.Newobj, paramCtor);
            cctorIL.Emit(OpCodes.Stloc, paramLocal);

            //Set parameter name
            cctorIL.Emit(OpCodes.Ldloc, paramLocal);
            cctorIL.Emit(OpCodes.Ldstr, paramName);
            cctorIL.Emit(OpCodes.Callvirt, ModuleDefinition.Import(typeof(AspectMethodParameterInfo).GetProperty("Name").GetSetMethod()));

            //Set parameter isRef
            cctorIL.Emit(OpCodes.Ldloc, paramLocal);
            cctorIL.Emit(OpCodes.Ldc_I4, param.ParameterType.IsByReference ? 1 : 0);
            cctorIL.Emit(OpCodes.Callvirt, ModuleDefinition.Import(typeof(AspectMethodParameterInfo).GetProperty("IsRef").GetSetMethod()));

            //Set parameter type
            cctorIL.Emit(OpCodes.Ldloc, paramLocal);
            if ((param.ParameterType.ContainsGenericParameter || param.ParameterType.HasGenericParameters || param.ParameterType.IsGenericParameter))
            {
                cctorIL.Emit(OpCodes.Ldnull);
            }
            else
            {
                cctorIL.Emit(OpCodes.Ldtoken, param.ParameterType);
                cctorIL.Emit(OpCodes.Call, ModuleDefinition.Import(typeof(Type).GetMethod("GetTypeFromHandle", new Type[] { typeof(RuntimeTypeHandle) })));
            }

            cctorIL.Emit(OpCodes.Callvirt, ModuleDefinition.Import(typeof(AspectMethodParameterInfo).GetProperty("Type").GetSetMethod()));

            //Set parameter attributes
            cctorIL.Emit(OpCodes.Ldloc, paramLocal);
            cctorIL.Emit(OpCodes.Ldtoken, method);
            if (isGeneric)
            {
                cctorIL.Emit(OpCodes.Ldtoken, declaringType);
            }
            cctorIL.Emit(OpCodes.Call, ModuleDefinition.Import(typeof(System.Reflection.MethodBase).GetMethod("GetMethodFromHandle",
                isGeneric ? new Type[] { typeof(RuntimeMethodHandle), typeof(RuntimeTypeHandle) } : new Type[] { typeof(RuntimeMethodHandle) }
                )));
            cctorIL.Emit(OpCodes.Ldc_I4, param.Index);
            cctorIL.Emit(OpCodes.Call, ModuleDefinition.Import(typeof(ModuleWeaver).GetMethod("GetParameterAttributes")));
            cctorIL.Emit(OpCodes.Callvirt, ModuleDefinition.Import(typeof(AspectMethodParameterInfo).GetProperty("Attributes").GetSetMethod()));

            //Add parameter to list
            cctorIL.Emit(OpCodes.Ldloc, paramsLocal);
            cctorIL.Emit(OpCodes.Ldloc, paramLocal);
            cctorIL.Emit(OpCodes.Callvirt, paramAdd);
        }


        cctorIL.Emit(OpCodes.Ldloc, paramsLocal);
        cctorIL.Emit(OpCodes.Newobj, ModuleDefinition.Import(typeof(ReadOnlyCollection<AspectMethodParameterInfo>).GetConstructor( new[] { typeof(IList<AspectMethodParameterInfo>) })));
        cctorIL.Emit(OpCodes.Stsfld, paramsField);

        var oldBody = method.Body;
        var newBody = new MethodBody(method);
        var ret = Instruction.Create(OpCodes.Ret);
        ret.SequencePoint = oldBody.Instructions.Last().SequencePoint;
        Instruction local = null;
        VariableDefinition result = returnType == TypeSystem.Void ? null : new VariableDefinition(returnType);
        var instructions = oldBody.Instructions;
        var count = instructions.Count;
        var startIndex = 0;
        var usedVariables = new List<VariableDefinition>();
        var usedHash = new HashSet<int>();
        
        
        var il = newBody.GetILProcessor();

        //Add initial logic for constructors
        if (!declaringType.IsSequentialLayout && declaringType.IsClass && method.IsConstructor)
        {
            for (var i = 0; i < count; i++)
            {
                var instruction = instructions[i];
                var opCode = instruction.OpCode.ToString();
                var variable = instruction.Operand as VariableReference;
                
                if(variable != null && !usedHash.Contains(variable.Index))
                {
                    usedHash.Add(variable.Index);
                    usedVariables.Add(variable.Resolve());
                }else if(opCode.IndexOf("stloc", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var token = opCode.Split('.');
                    var index = Int32.Parse(token[1]);
                    if (!usedHash.Contains(index))
                    {
                        usedHash.Add(index);
                        usedVariables.Add(oldBody.Variables[index]);
                    }
                }

                if (instruction.OpCode == OpCodes.Call && (instruction.Operand as MethodReference).Resolve().IsConstructor)
                {
                    startIndex = i + 1;
                    il.Append(instruction);
                    break;
                }
                il.Append(instruction);
            }
        }

        if (origMethod != null)
        {
            oldBody.Variables.Clear();

            foreach (var variable in usedVariables)
            {
                newBody.Variables.Add(variable);
            }
        }else
        {
            foreach(var variable in oldBody.Variables)
            {
                newBody.Variables.Add(variable);
            }
        }

        //Add new variable for result if needed
        if (result != null)
        {
            newBody.Variables.Add(result);
        }


        //Define method context
        var aspectMethodInfo = ModuleDefinition.Import(typeof(AspectMethodInfo));
        var context = new VariableDefinition(aspectMethodInfo);
        var aspectMethodInfoCtor = ModuleDefinition.Import(aspectMethodInfo.Resolve().GetConstructors().First());

        //Add variable for context
        newBody.Variables.Add(context);

        //Create context instance
        il.Emit(OpCodes.Newobj, aspectMethodInfoCtor);
        il.Emit(OpCodes.Stloc, context);

        //Set method info to context
        il.Emit(OpCodes.Ldloc, context);
        il.Emit(OpCodes.Ldsfld, methodField);
        il.Emit(OpCodes.Callvirt, ModuleDefinition.Import(typeof(AspectMethodInfo).GetProperty("Method").GetSetMethod()));

        //Set method attributes to context
        il.Emit(OpCodes.Ldloc, context);
        il.Emit(OpCodes.Ldsfld, methodAttributesField);
        il.Emit(OpCodes.Callvirt, ModuleDefinition.Import(typeof(AspectMethodInfo).GetProperty("Attributes").GetSetMethod()));

        //Set instance to context
        if (!isStatic)
        {
            il.Emit(OpCodes.Ldloc, context);
            il.Emit(OpCodes.Ldarg_0);
            if (declaringType.IsValueType)
            {
                il.Emit(OpCodes.Ldobj, declaringType);
                il.Emit(OpCodes.Box, declaringType);
            }

            il.Emit(OpCodes.Callvirt, ModuleDefinition.Import(typeof(AspectMethodInfo).GetProperty("Instance").GetSetMethod()));
        }

        //Set parameters to context
        il.Emit(OpCodes.Ldloc, context);
        il.Emit(OpCodes.Ldsfld, paramsField);
        il.Emit(OpCodes.Callvirt, ModuleDefinition.Import(typeof(AspectMethodInfo).GetProperty("Parameters").GetSetMethod()));

        var argumentsLocal = new VariableDefinition(ModuleDefinition.Import(typeof(List<object>)));
        newBody.Variables.Add(argumentsLocal);
        var argumentsAdd = ModuleDefinition.Import(typeof(List<object>).GetMethod("Add", new[] { typeof(object) }));

        il.Emit(OpCodes.Ldloc, context);
        il.Emit(OpCodes.Callvirt, ModuleDefinition.Import(typeof(AspectMethodInfo).GetProperty("Arguments").GetGetMethod()));
        il.Emit(OpCodes.Stloc, argumentsLocal);

        //Set arguments to context
        foreach (var param in method.Parameters)
        {
            //Set argument value
            il.Emit(OpCodes.Ldloc, argumentsLocal);
            if (param.IsOut)
            {
                il.Emit(OpCodes.Ldnull);
            }
            else
            {
                il.Emit(OpCodes.Ldarg, param.Index + parameterOffset);
                if (param.ParameterType.IsByReference)
                {
                    var elementType = param.ParameterType.GetElementType();
                    if (!elementType.IsValueType)
                    {
                        il.Emit(OpCodes.Ldind_Ref);
                    }
                    else if (elementType.FullName == ModuleDefinition.Import(typeof(int)).FullName)
                    {
                        il.Emit(OpCodes.Ldind_I4);
                    }
                    else if (elementType.FullName == ModuleDefinition.Import(typeof(byte)).FullName)
                    {
                        il.Emit(OpCodes.Ldind_I1);
                    }
                    else if (elementType.FullName == ModuleDefinition.Import(typeof(short)).FullName)
                    {
                        il.Emit(OpCodes.Ldind_I2);
                    }
                    else if (elementType.FullName == ModuleDefinition.Import(typeof(long)).FullName)
                    {
                        il.Emit(OpCodes.Ldind_I8);
                    }
                    else if (elementType.FullName == ModuleDefinition.Import(typeof(float)).FullName)
                    {
                        il.Emit(OpCodes.Ldind_R4);
                    }
                    else if (elementType.FullName == ModuleDefinition.Import(typeof(double)).FullName)
                    {
                        il.Emit(OpCodes.Ldind_R8);
                    }
                    else if (elementType.FullName == ModuleDefinition.Import(typeof(ushort)).FullName)
                    {
                        il.Emit(OpCodes.Ldind_U2);
                    }
                    else if (elementType.FullName == ModuleDefinition.Import(typeof(uint)).FullName)
                    {
                        il.Emit(OpCodes.Ldind_U4);
                    }

                    if (elementType.IsValueType)
                    {
                        il.Emit(OpCodes.Box, elementType);
                    }
                }

                if (param.ParameterType.IsValueType || (param.ParameterType.ContainsGenericParameter || param.ParameterType.HasGenericParameters || param.ParameterType.IsGenericParameter))
                {
                    il.Emit(OpCodes.Box, param.ParameterType);
                }
            }

            il.Emit(OpCodes.Callvirt, argumentsAdd);
        }

        //Call on intercept for context
        foreach (var attr in methAttrs)
        {
            FieldDefinition def;
            if (aspects.TryGetValue(attr.AttributeType.FullName, out def))
            {
                il.Emit(OpCodes.Ldsfld, def);
                il.Emit(OpCodes.Ldloc, context);
                il.Emit(OpCodes.Callvirt, ModuleDefinition.Import(typeof(BaseAspect).GetMethod("OnEnter")));
            }
        }

        var startFinally = Instruction.Create(OpCodes.Nop);

        il.Append(startFinally);

        //Call clone method here
        var startInvoke = Instruction.Create(OpCodes.Nop);
        var continueLabel = Instruction.Create(OpCodes.Nop);

        il.Append(startInvoke);

        //Execute if continue of context is true
        il.Emit(OpCodes.Ldloc, context);
        il.Emit(OpCodes.Callvirt, ModuleDefinition.Import(typeof(AspectMethodInfo).GetProperty("Continue").GetGetMethod()));
        il.Emit(OpCodes.Brfalse, continueLabel);

        //Load this
        if (!isStatic)
        {
            il.Emit(OpCodes.Ldarg_0);
        }

        //Load Arguments
        for (var i = 0; i < method.Parameters.Count; i++)
        {
            il.Emit(OpCodes.Ldarg, parameterOffset + i);
        }

        //Invoke clone
        if (origMethod != null)
        {
            var cloneMethod = ToGenericMethod(origMethod, declaringType);
            var invokeInstruction = Instruction.Create(isStatic || declaringType.IsValueType ? OpCodes.Call : OpCodes.Callvirt, cloneMethod);

            il.Append(invokeInstruction);
        }else
        {
            instructions = new Mono.Collections.Generic.Collection<Instruction>(instructions.Skip(startIndex).ToList());
            instructions = new Mono.Collections.Generic.Collection<Instruction>(instructions.Take(instructions.Count - 1).ToList());
            foreach(var instruction in instructions)
            {
                il.Append(instruction);
            }
        }


        if (result == null)
        {
            //Call on success for context
            foreach (var attr in methAttrs)
            {
                FieldDefinition def;
                if (aspects.TryGetValue(attr.AttributeType.FullName, out def))
                {
                    il.Emit(OpCodes.Ldsfld, def);
                    il.Emit(OpCodes.Ldloc, context);
                    il.Emit(OpCodes.Callvirt, ModuleDefinition.Import(typeof(BaseAspect).GetMethod("OnSuccess")));
                }
            }

            //end of continue check
            il.Append(continueLabel);
        }
        else if(result != null)
        {
            //Create ldloc for local
            local = Instruction.Create(OpCodes.Ldloc, result);

            //Store result
            il.Emit(OpCodes.Stloc, result);


            //Set continuation for exception
            if (isTask)
            {
                MethodReference handleTaskError = ModuleDefinition.Import(typeof(ModuleWeaver).GetMethod("HandleTaskError"));
                
                //handle on error for task
                foreach (var attr in methAttrs)
                {
                    FieldDefinition def;
                    if (aspects.TryGetValue(attr.AttributeType.FullName, out def))
                    {
                        il.Emit(OpCodes.Ldsfld, def);
                        il.Emit(OpCodes.Ldloc, context);
                        il.Emit(OpCodes.Ldloc, result);
                        il.Emit(OpCodes.Call, handleTaskError);
                        il.Emit(OpCodes.Stloc, result);
                    }
                }
            }

            //Set result to context
            il.Emit(OpCodes.Ldloc, context);
            il.Emit(OpCodes.Ldloc, result);

            if (returnType.IsValueType || (returnType.ContainsGenericParameter || returnType.HasGenericParameters || returnType.IsGenericInstance))
            {
                il.Emit(OpCodes.Box, returnType);
            }

            il.Emit(OpCodes.Callvirt, ModuleDefinition.Import(typeof(AspectMethodInfo).GetProperty("Returns").GetSetMethod()));

            if (isTask)
            {
                MethodReference handleTaskSuccess = ModuleDefinition.Import(typeof(ModuleWeaver).GetMethod("HandleTaskSuccess"));

                //Handle on success for task
                foreach (var attr in methAttrs)
                {
                    FieldDefinition def;
                    if (aspects.TryGetValue(attr.AttributeType.FullName, out def))
                    {
                        il.Emit(OpCodes.Ldsfld, def);
                        il.Emit(OpCodes.Ldloc, context);
                        il.Emit(OpCodes.Ldloc, result);
                        il.Emit(OpCodes.Call, handleTaskSuccess);
                        il.Emit(OpCodes.Stloc, result);
                    }
                }
            }
            else
            {
                //Call on success for context
                foreach (var attr in methAttrs)
                {
                    FieldDefinition def;
                    if (aspects.TryGetValue(attr.AttributeType.FullName, out def))
                    {
                        il.Emit(OpCodes.Ldsfld, def);
                        il.Emit(OpCodes.Ldloc, context);
                        il.Emit(OpCodes.Callvirt, ModuleDefinition.Import(typeof(BaseAspect).GetMethod("OnSuccess")));
                    }
                }
            }

            if(isTask)
            {
                MethodReference handleTaskExit = ModuleDefinition.Import(typeof(ModuleWeaver).GetMethod("HandleTaskExit"));

                //Handle on exit for task
                foreach (var attr in methAttrs.Reverse())
                {
                    FieldDefinition def;
                    if (aspects.TryGetValue(attr.AttributeType.FullName, out def))
                    {
                        il.Emit(OpCodes.Ldsfld, def);
                        il.Emit(OpCodes.Ldloc, context);
                        il.Emit(OpCodes.Ldloc, result);
                        il.Emit(OpCodes.Call, handleTaskExit);
                        il.Emit(OpCodes.Stloc, result);
                    }
                }
            }

            //end of continue check
            il.Append(continueLabel);

            //Update return value if needed
            if (!isTask)
            {
                GenericInstanceMethod genReturn = new GenericInstanceMethod(ModuleDefinition.Import(typeof(ModuleWeaver).GetMethod("GetReturn")));
                genReturn.GenericArguments.Add(returnType);

                il.Emit(OpCodes.Ldloc, context);
                il.Emit(OpCodes.Call, genReturn);
                il.Emit(OpCodes.Stloc, result);
            }
        }

        il.Emit(OpCodes.Leave, local ?? ret);

        //catch
        var catRet = Instruction.Create(OpCodes.Nop);
        var exLocal = new VariableDefinition(ModuleDefinition.Import(typeof(Exception)));
        newBody.Variables.Add(exLocal);
        il.Append(catRet);
        il.Emit(OpCodes.Stloc, exLocal);

        //Set exception to context
        il.Emit(OpCodes.Ldloc, context);
        il.Emit(OpCodes.Ldloc, exLocal);
        il.Emit(OpCodes.Callvirt, ModuleDefinition.Import(typeof(AspectMethodInfo).GetProperty("Exception").GetSetMethod()));

        //Call on error for context
        foreach (var attr in methAttrs)
        {
            FieldDefinition def;
            if (aspects.TryGetValue(attr.AttributeType.FullName, out def))
            {
                il.Emit(OpCodes.Ldsfld, def);
                il.Emit(OpCodes.Ldloc, context);
                il.Emit(OpCodes.Callvirt, ModuleDefinition.Import(typeof(BaseAspect).GetMethod("OnError")));
            }
        }

        var throwLabel = Instruction.Create(OpCodes.Nop);

        il.Emit(OpCodes.Ldloc, context);
        il.Emit(OpCodes.Callvirt, ModuleDefinition.Import(typeof(AspectMethodInfo).GetProperty("ReThrow").GetGetMethod()));
        il.Emit(OpCodes.Brfalse, throwLabel);

        il.Emit(OpCodes.Rethrow);
        
        il.Append(throwLabel);

        var endCatch = Instruction.Create(OpCodes.Leave, local ?? ret);

        il.Emit(OpCodes.Leave, endCatch);
        
        il.Append(endCatch);
        
        //finally
        var finallyRet = Instruction.Create(OpCodes.Nop);
        il.Append(finallyRet);

        if (!isTask)
        {
            //Call on exit for context
            foreach (var attr in methAttrs.Reverse())
            {
                FieldDefinition def;
                if (aspects.TryGetValue(attr.AttributeType.FullName, out def))
                {
                    il.Emit(OpCodes.Ldsfld, def);
                    il.Emit(OpCodes.Ldloc, context);
                    il.Emit(OpCodes.Callvirt, ModuleDefinition.Import(typeof(BaseAspect).GetMethod("OnExit")));
                    var nop = Instruction.Create(OpCodes.Nop);
                    var lastItem = instructions.LastOrDefault(x => x.SequencePoint != null);
                    if (lastItem != null)
                    {
                        nop.SequencePoint = lastItem.SequencePoint;
                    }
                    il.Append(nop);
                }
            }
        }

        il.Emit(OpCodes.Endfinally);

        if (result != null)
        {
            il.Append(local);
        }

        il.Append(ret);

        var @catch = new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = startInvoke,
            TryEnd = catRet,
            HandlerStart = catRet,
            HandlerEnd = endCatch,
            CatchType = ModuleDefinition.Import(typeof(Exception))
        };

        var @finally = new ExceptionHandler(ExceptionHandlerType.Finally)
        {
            TryStart = startFinally,
            TryEnd = finallyRet,
            HandlerStart = finallyRet,
            HandlerEnd = local ?? ret
        };

        newBody.ExceptionHandlers.Add(@catch);
        newBody.ExceptionHandlers.Add(@finally);
        newBody.InitLocals = true;
        newBody.OptimizeMacros();

        method.Body = newBody;
        // Mark the re-written method (if not already marked) so that the debugger doesn't try to stop in generated code.
        if (!method.CustomAttributes.Any(attr => attr.Constructor.FullName == debuggerStepThroughAttributeCtor.FullName))
        {
            method.CustomAttributes.Add(new CustomAttribute(debuggerStepThroughAttributeCtor));
        }
    }

    /// <summary>
    /// Convert method to generic call if needed
    /// </summary>
    /// <param name="method">Method</param>
    /// <param name="type">Type</param>
    /// <returns>MethodReference</returns>
    private static MethodReference ToGenericMethod(MethodReference method, TypeReference type)
    {
        var convertedMethod = method;
        if (method.HasGenericParameters || type.HasGenericParameters)
        {
            var genParameters = method.GenericParameters;
            var parameters = convertedMethod.Parameters;

            if (type.HasGenericParameters)
            {
                var typeGenParameters = type.GenericParameters;
                var genericType = type.MakeGenericInstanceType(typeGenParameters.ToArray());

                convertedMethod = new MethodReference(method.Name, method.ReturnType, genericType);
                convertedMethod.CallingConvention = method.CallingConvention;
                convertedMethod.HasThis = method.HasThis;

                foreach (var parameter in parameters)
                {
                    convertedMethod.Parameters.Add(parameter);
                }

                foreach (var genArg in genParameters)
                {
                    convertedMethod.GenericParameters.Add(new GenericParameter(genArg.Name, convertedMethod));
                }
            }

            if (method.HasGenericParameters)
            {
                convertedMethod = new GenericInstanceMethod(convertedMethod);

                foreach (var genArg in genParameters)
                {
                    (convertedMethod as GenericInstanceMethod).GenericArguments.Add(genArg);
                }
            }
        }

        return convertedMethod;
    }

    /// <summary>
    /// Format data to correct type
    /// </summary>
    /// <param name="arg">Argument</param>
    /// <returns></returns>
    private static object FormatValue(CustomAttributeArgument arg)
    {
        var value = arg.Value;
        var ctorParameterType = arg.Type;
        if (ctorParameterType.Resolve().IsEnum)
        {
            return Enum.Parse(Type.GetType(ctorParameterType.FullName, true), value.ToString());
        }
        return value;
    }

    /// <summary>
    /// Create unique custom attributes for the collection of custom attributes
    /// </summary>
    /// <param name="attrs">Attributes</param>
    /// <returns>IEnumerable{CustomAttribute}</returns>
    private static IEnumerable<CustomAttribute> UniqueAttributes(IEnumerable<CustomAttribute> attrs)
    {
        var hash = new HashSet<string>();

        foreach(var attr in attrs)
        {
            var name = attr.AttributeType.FullName;
            if (!hash.Contains(name))
            {
                hash.Add(name);
                yield return attr;
            }
        }
    }

    /// <summary>
    /// Create unique custom attributes for the collection of custom attributes
    /// </summary>
    /// <param name="attrs">Attributes</param>
    /// <returns>IEnumerable{CustomAttribute}</returns>
    private static IEnumerable<Attribute> UniqueAttributes(IEnumerable<Attribute> attrs)
    {
        var hash = new HashSet<string>();

        foreach (var attr in attrs)
        {
            var name = attr.GetType().FullName;
            if (!hash.Contains(name))
            {
                hash.Add(name);
                yield return attr;
            }
        }
    }
}
 