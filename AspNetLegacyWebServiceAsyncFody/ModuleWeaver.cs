using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Services;
using Assembly = System.Reflection.Assembly;

public class ModuleWeaver
{
    public ModuleDefinition ModuleDefinition { get; set; }

    private TypeReference webServiceType;
    private TypeReference taskBaseType;
    private TypeReference taskOpenType;
    private MethodReference generatedCodeAttributeCtor;
    private CustomAttribute generatedCodeAttr;
    private CustomAttribute debuggerStepThroughAttr;
    private TypeReference iAsyncResultType;
    private TypeReference asyncCallbackType;
    private TypeReference taskCompletionSourceType;
    private MethodReference objectCtor;
    private TypeReference iEnumerableExceptionType;
    private TypeReference aggregateExceptionType;
    private MethodReference get_InnerExceptionsMethod;
    private MethodReference task_get_IsFaultedMethod;
    private MethodReference task_get_ExceptionMethod;
    private MethodReference task_get_IsCanceledMethod;
    private MethodReference task_WaitMethod;
    private MethodReference asyncCallbackInvokeMethod;
    private TypeReference actionOpenType;
    private MethodReference get_InnerExceptionMethod;
    private MethodReference exceptionDispatchInfoCaptureMethod;
    private MethodReference exceptionDispatchInfoThrowMethod;

    private ContinuationClassInfo contClassForVoid;
    private ContinuationClassInfo contClassForNonVoid;

    public void Execute()
    {
        webServiceType = ModuleDefinition.ImportReference(typeof(WebService));
        taskBaseType = ModuleDefinition.ImportReference(typeof(Task));
        taskOpenType = ModuleDefinition.ImportReference(typeof(Task<>));
        iAsyncResultType = ModuleDefinition.ImportReference(typeof(IAsyncResult));
        asyncCallbackType = ModuleDefinition.ImportReference(typeof(AsyncCallback));
        taskCompletionSourceType = ModuleDefinition.ImportReference(typeof(TaskCompletionSource<>));
        objectCtor = ModuleDefinition.ImportReference(typeof(object).GetConstructor(new Type[0]));
        iEnumerableExceptionType = ModuleDefinition.ImportReference(typeof(IEnumerable<Exception>));
        aggregateExceptionType = ModuleDefinition.ImportReference(typeof(AggregateException));
        get_InnerExceptionsMethod = ModuleDefinition.ImportReference(typeof(AggregateException).GetMethod("get_InnerExceptions", new Type[0]));
        task_get_IsFaultedMethod = new MethodReference("get_IsFaulted", ModuleDefinition.TypeSystem.Boolean, taskBaseType) { HasThis = true };
        task_get_ExceptionMethod = new MethodReference("get_Exception", aggregateExceptionType, taskBaseType) { HasThis = true };
        task_get_IsCanceledMethod = new MethodReference("get_IsCanceled", ModuleDefinition.TypeSystem.Boolean, taskBaseType) { HasThis = true };
        task_WaitMethod = new MethodReference("Wait", ModuleDefinition.TypeSystem.Void, taskBaseType) { HasThis = true };
        asyncCallbackInvokeMethod = new MethodReference("Invoke", ModuleDefinition.TypeSystem.Void, asyncCallbackType) { HasThis = true };
        asyncCallbackInvokeMethod.Parameters.Add(new ParameterDefinition(iAsyncResultType));
        actionOpenType = ModuleDefinition.ImportReference(typeof(Action<>));
        get_InnerExceptionMethod = ModuleDefinition.ImportReference(typeof(Exception).GetMethod("get_InnerException", new Type[0]));
        exceptionDispatchInfoCaptureMethod = ModuleDefinition.ImportReference(
            ((Func<Exception, ExceptionDispatchInfo>)ExceptionDispatchInfo.Capture).Method);
        exceptionDispatchInfoThrowMethod = new MethodReference("Throw", ModuleDefinition.TypeSystem.Void,
            exceptionDispatchInfoCaptureMethod.DeclaringType) { HasThis = true };

        generatedCodeAttributeCtor = ModuleDefinition.ImportReference(
            typeof(GeneratedCodeAttribute).GetConstructor(new Type[] { typeof(string), typeof(string) }));
        generatedCodeAttr = new CustomAttribute(generatedCodeAttributeCtor);
        generatedCodeAttr.ConstructorArguments.Add( // tool
            new CustomAttributeArgument(ModuleDefinition.TypeSystem.String, "AspNetLegacyWebServiceAsync.Fody"));
        generatedCodeAttr.ConstructorArguments.Add( // version
            new CustomAttributeArgument(ModuleDefinition.TypeSystem.String, 
            Assembly.GetExecutingAssembly().GetName().Version.ToString()));

        var debuggerStepThroughCtor = ModuleDefinition.ImportReference(
            typeof(DebuggerStepThroughAttribute).GetConstructor(new Type[0]));
        debuggerStepThroughAttr = new CustomAttribute(debuggerStepThroughCtor);

        var orgTypes = ModuleDefinition.Types.ToList();
        foreach (var type in orgTypes)
        {
            if (IsDescendedFrom(webServiceType, type))
                ModifyWebService(type);
        }
    }

    private void ModifyWebService(TypeDefinition type)
    {
        var orgMethods = type.Methods.ToList();
        foreach (var method in orgMethods)
        {
            if (!method.IsPublic)
                continue;
            TypeReference taskType = method.ReturnType;
            TypeReference taskReturnType = GetTaskReturnType(taskType);
            if (taskReturnType == null)
                continue;
            List<CustomAttribute> webServiceAttrs = new List<CustomAttribute>();
            bool isWebMethod = false;
            foreach (var attr in method.CustomAttributes)
            {
                if (attr.AttributeType.FullName == "System.Web.Services.WebMethodAttribute")
                {
                    webServiceAttrs.Add(attr);
                    isWebMethod = true;
                }
                else if (attr.AttributeType.FullName == "System.Web.Services.Protocols.SoapDocumentMethodAttribute" ||
                    attr.AttributeType.FullName == "System.Web.Services.Protocols.SoapRpcMethodAttribute")
                    webServiceAttrs.Add(attr);
            }
            if (!isWebMethod)
                continue;

            bool isVoid = taskReturnType.MetadataType == MetadataType.Void;

            foreach (var attr in webServiceAttrs)
                method.CustomAttributes.Remove(attr);
            string orgMethodName = method.Name;
            method.Name += "<>Async";
            method.Attributes &= ~MethodAttributes.Public;
            method.Attributes |= MethodAttributes.Private;

            var typeSystem = type.Module.TypeSystem;

            var ci = GetContinuationClass(type.Module, taskReturnType);
            var tcsType = new GenericInstanceType(((GenericInstanceType)ci.TcsField.FieldType).ElementType);
            if (!isVoid)
                tcsType.GenericArguments.Add(taskReturnType);
            else
                tcsType.GenericArguments.Add(typeSystem.Object);
            var tcsCtor = new MethodReference(".ctor", typeSystem.Void, tcsType) { HasThis = true };
            tcsCtor.Parameters.Add(new ParameterDefinition(typeSystem.Object));
            var actionType = new GenericInstanceType(actionOpenType);
            actionType.GenericArguments.Add(taskType);
            var actionTypeCtor = new MethodReference(".ctor", typeSystem.Void, actionType) { HasThis = true };
            actionTypeCtor.Parameters.Add(new ParameterDefinition(typeSystem.Object));
            actionTypeCtor.Parameters.Add(new ParameterDefinition(typeSystem.IntPtr));
            var continueWith = new MethodReference("ContinueWith", taskBaseType, taskType) { HasThis = true };
            TypeReference taskSelfInst;
            if (isVoid)
                taskSelfInst = taskBaseType;
            else
            {
                taskSelfInst = new GenericInstanceType(taskOpenType);
                ((GenericInstanceType)taskSelfInst).GenericArguments.Add(taskOpenType.GenericParameters[0]);
            }
            var actionTypeForContinueWith = new GenericInstanceType(actionOpenType);
            actionTypeForContinueWith.GenericArguments.Add(taskSelfInst);
            continueWith.Parameters.Add(new ParameterDefinition(actionTypeForContinueWith));
            var tcsInstTask = new GenericInstanceType(taskOpenType);
            tcsInstTask.GenericArguments.Add(taskCompletionSourceType.GenericParameters[0]);
            var get_Task = new MethodReference("get_Task", tcsInstTask, tcsType) { HasThis = true };
            MethodReference get_Result = null;
            if (!isVoid)
                get_Result = new MethodReference("get_Result", taskOpenType.GenericParameters[0], taskType) { HasThis = true };

            var beginMethod = new MethodDefinition("Begin" + orgMethodName, MethodAttributes.Public , iAsyncResultType);
            beginMethod.Body.InitLocals = true;
            foreach (var attr in webServiceAttrs)
                beginMethod.CustomAttributes.Add(attr);
            beginMethod.CustomAttributes.Add(debuggerStepThroughAttr);
            beginMethod.CustomAttributes.Add(generatedCodeAttr);
            foreach (var arg in method.Parameters)
                beginMethod.Parameters.Add(arg);

            var callbackArg = new ParameterDefinition(asyncCallbackType);
            var stateArg = new ParameterDefinition(ModuleDefinition.TypeSystem.Object);
            beginMethod.Parameters.Add(callbackArg); // callback
            beginMethod.Parameters.Add(stateArg); // state

            beginMethod.Body.Variables.Add(new VariableDefinition(actionType)); // actionDelegate
            var il = beginMethod.Body.GetILProcessor();
            // tmp = new <asyncContinuationClass><T>()
            il.Emit(OpCodes.Newobj, ci.Constructor);
            // tmp.Callback = callback
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldarg, callbackArg);
            il.Emit(OpCodes.Stfld, ci.CallbackField);
            // tmp.Tcs = new TaskCompletionSource<T>(state)
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldarg, stateArg);
            il.Emit(OpCodes.Newobj, tcsCtor);
            il.Emit(OpCodes.Stfld, ci.TcsField);
            // actionDelegate = new Action<Task<T>>(tmp.Invoke)
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldftn, ci.InvokeMethod);
            il.Emit(OpCodes.Newobj, actionTypeCtor);
            il.Emit(OpCodes.Stloc_0);
            // method(arg1, arg2, ...).ContinueWith(new Action<Task<T>>(tmp.Invoke))
            il.Emit(OpCodes.Ldarg_0);
            for (int i = 0; i < method.Parameters.Count; i++)
                il.Emit(OpCodes.Ldarg, i+1);
            il.Emit(OpCodes.Call, method); // call method(...)
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Callvirt, continueWith);
            il.Emit(OpCodes.Pop); // ignore return value

            // return tcs.Task
            il.Emit(OpCodes.Ldfld, ci.TcsField);
            il.Emit(OpCodes.Callvirt, get_Task);
            il.Emit(OpCodes.Ret);

            type.Methods.Add(beginMethod);

            var endMethod = new MethodDefinition("End" + orgMethodName, MethodAttributes.Public, taskReturnType);
            endMethod.Body.InitLocals = true;
            foreach (var attr in webServiceAttrs)
                endMethod.CustomAttributes.Add(attr);
            endMethod.CustomAttributes.Add(debuggerStepThroughAttr);
            endMethod.CustomAttributes.Add(generatedCodeAttr);

            endMethod.Parameters.Add(new ParameterDefinition(iAsyncResultType));

            if (!isVoid)
                endMethod.Body.Variables.Add(new VariableDefinition(taskReturnType));

            il = endMethod.Body.GetILProcessor();
            var eh = new ExceptionHandler(ExceptionHandlerType.Catch);
            eh.CatchType = aggregateExceptionType;
            endMethod.Body.ExceptionHandlers.Add(eh);
            eh.TryStart = Instruction.Create(OpCodes.Ldarg_1);
            il.Append(eh.TryStart);
            il.Emit(OpCodes.Castclass, taskType);
            if (isVoid)
                il.Emit(OpCodes.Callvirt, task_WaitMethod);
            else
                il.Emit(OpCodes.Callvirt, get_Result);
            if (!isVoid)
                il.Emit(OpCodes.Stloc_0);
            var endInstr = isVoid ? Instruction.Create(OpCodes.Ret) : Instruction.Create(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Leave_S, endInstr);

            eh.HandlerStart = Instruction.Create(OpCodes.Callvirt, get_InnerExceptionMethod);
            eh.TryEnd = eh.HandlerStart;
            il.Append(eh.HandlerStart);
            il.Emit(OpCodes.Call, exceptionDispatchInfoCaptureMethod);
            il.Emit(OpCodes.Callvirt, exceptionDispatchInfoThrowMethod);
            // This makes the verfier happe so it doesn't thinks we could fall through the catch block
            il.Emit(OpCodes.Rethrow);

            eh.HandlerEnd = endInstr;
            il.Append(endInstr);
            if (!isVoid)
                il.Emit(OpCodes.Ret);

            type.Methods.Add(endMethod);
        }
    }

    private ContinuationClassInfo GetContinuationClass(ModuleDefinition module, TypeReference taskReturnType)
    {
        if (taskReturnType.MetadataType == MetadataType.Void)
            return GetContinuationClassCore(module, true);

        ContinuationClassInfo genClassInfo = GetContinuationClassCore(module, false);
        GenericInstanceType instType = new GenericInstanceType(genClassInfo.ContinuationClass);
        instType.GenericArguments.Add(taskReturnType);
        return new ContinuationClassInfo()
        {
            ContinuationClass = instType,
            CallbackField = GetReferenceForInstantiatedType(genClassInfo.CallbackField, instType),
            TcsField = GetReferenceForInstantiatedType(genClassInfo.TcsField, instType),
            Constructor = GetReferenceForInstantiatedType(genClassInfo.Constructor, instType),
            InvokeMethod = GetReferenceForInstantiatedType(genClassInfo.InvokeMethod, instType)
        };
    }

    private FieldReference GetReferenceForInstantiatedType(FieldReference field, GenericInstanceType instType)
    {
        return new FieldReference(field.Name, field.FieldType, instType);
    }
    private MethodReference GetReferenceForInstantiatedType(MethodReference method, GenericInstanceType instType)
    {
        var m = new MethodReference(method.Name, method.ReturnType, instType)
        {
            CallingConvention = method.CallingConvention,
            ExplicitThis = method.ExplicitThis,
            HasThis = method.HasThis
        };
        foreach (var arg in method.Parameters)
            m.Parameters.Add(arg);
        var genMethod = method as GenericInstanceMethod;
        if (genMethod == null)
            return m;
        var gm = new GenericInstanceMethod(m);
        foreach (var ga in genMethod.GenericArguments)
            gm.GenericArguments.Add(ga);
        return gm;
    }

    private ContinuationClassInfo GetContinuationClassCore(ModuleDefinition module, bool isVoid)
    {
        if (isVoid && contClassForVoid != null)
            return contClassForVoid;
        if (!isVoid && contClassForNonVoid != null)
            return contClassForNonVoid;

        ContinuationClassInfo ci = new ContinuationClassInfo();
        TypeDefinition classDef = new TypeDefinition("", "<asyncContinuationClass>" + (isVoid ? "" : "`1"),
            TypeAttributes.Sealed,  module.TypeSystem.Object);
        classDef.CustomAttributes.Add(generatedCodeAttr);
        TypeReference taskReturnType, selfInst;
        if (!isVoid)
        {
            classDef.GenericParameters.Add(new GenericParameter("T", classDef));
            taskReturnType = classDef.GenericParameters[0];
            selfInst = new GenericInstanceType(classDef);
            ((GenericInstanceType)selfInst).GenericArguments.Add(classDef.GenericParameters[0]);
        }
        else
        {
            taskReturnType = module.TypeSystem.Object;
            selfInst = classDef;
        }

        ci.ContinuationClass = classDef;

        GenericInstanceType genTaskType = new GenericInstanceType(taskOpenType);
        genTaskType.GenericArguments.Add(taskReturnType);
        TypeReference taskType = isVoid ? taskBaseType : genTaskType;

        FieldDefinition callbackGen = new FieldDefinition("Callback", FieldAttributes.Public, asyncCallbackType);
        classDef.Fields.Add(callbackGen);
        ci.CallbackField = callbackGen;
        var callback = new FieldReference(callbackGen.Name, callbackGen.FieldType, selfInst);

        var tcsType = new GenericInstanceType(taskCompletionSourceType);
        tcsType.GenericArguments.Add(isVoid ? module.TypeSystem.Object : taskReturnType);
        FieldDefinition tcsGen = new FieldDefinition("Tcs", FieldAttributes.Public, tcsType);
        classDef.Fields.Add(tcsGen);
        ci.TcsField = tcsGen;
        var tcs = new FieldReference(tcsGen.Name, tcsGen.FieldType, selfInst);

        var trySetExceptionRef = new MethodReference("TrySetException", module.TypeSystem.Boolean, tcsType) { HasThis = true };
        trySetExceptionRef.Parameters.Add(new ParameterDefinition(iEnumerableExceptionType));
        var trySetCanceledRef = new MethodReference("TrySetCanceled", module.TypeSystem.Boolean, tcsType) { HasThis = true };
        var trySetResultRef = new MethodReference("TrySetResult", module.TypeSystem.Boolean, tcsType) { HasThis = true };
        trySetResultRef.Parameters.Add(new ParameterDefinition(taskCompletionSourceType.GenericParameters[0]));
        var taskOfTcsTP0 = new GenericInstanceType(taskOpenType);
        taskOfTcsTP0.GenericArguments.Add(taskCompletionSourceType.GenericParameters[0]);
        var getTaskRef = new MethodReference("get_Task", taskOfTcsTP0, tcsType) { HasThis = true };

        MethodReference get_Result = null;
        if (!isVoid)
            get_Result = new MethodReference("get_Result", taskOpenType.GenericParameters[0], taskType) { HasThis = true };

        MethodDefinition ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);

        var il = ctor.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0); // this
        il.Emit(OpCodes.Call, objectCtor); // base()
        il.Emit(OpCodes.Ret);

        classDef.Methods.Add(ctor);
        ci.Constructor = ctor;

        MethodDefinition invoke = new MethodDefinition("Invoke", MethodAttributes.Public, module.TypeSystem.Void);
        var taskArg = new ParameterDefinition(taskType);
        invoke.Parameters.Add(taskArg);

        il = invoke.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_1); // task
        il.Emit(OpCodes.Callvirt, task_get_IsFaultedMethod);
        var notFaultedCont = Instruction.Create(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse_S, notFaultedCont);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, tcs);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, task_get_ExceptionMethod);
        il.Emit(OpCodes.Callvirt, get_InnerExceptionsMethod);
        il.Emit(OpCodes.Callvirt, trySetExceptionRef);
        il.Emit(OpCodes.Pop);
        var callbackCont = Instruction.Create(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Br_S, callbackCont);

        il.Append(notFaultedCont); // ldarg.1
        il.Emit(OpCodes.Callvirt, task_get_IsCanceledMethod);
        var notCanceledCont = Instruction.Create(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse_S, notCanceledCont);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, tcs);
        il.Emit(OpCodes.Callvirt, trySetCanceledRef);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Br_S, callbackCont);

        il.Append(notCanceledCont); // ldarg.0
        il.Emit(OpCodes.Ldfld, tcs);
        il.Emit(OpCodes.Ldarg_1);
        if (isVoid)
        {
            il.Emit(OpCodes.Callvirt, task_WaitMethod);
            il.Emit(OpCodes.Ldnull);
        }
        else
        {
            il.Emit(OpCodes.Callvirt, get_Result);
        }
        il.Emit(OpCodes.Callvirt, trySetResultRef);
        il.Emit(OpCodes.Pop);

        il.Append(callbackCont); // ldarg.0
        il.Emit(OpCodes.Ldfld, callback);
        il.Emit(OpCodes.Dup);
        var endPopRet = Instruction.Create(OpCodes.Pop);
        il.Emit(OpCodes.Brfalse_S, endPopRet);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, tcs);
        il.Emit(OpCodes.Callvirt, getTaskRef);
        il.Emit(OpCodes.Callvirt, asyncCallbackInvokeMethod);

        il.Emit(OpCodes.Ret);

        il.Append(endPopRet); // pop
        il.Emit(OpCodes.Ret);

        classDef.Methods.Add(invoke);
        ci.InvokeMethod = invoke;

        module.Types.Add(classDef);

        if (isVoid)
            contClassForVoid = ci;
        else
            contClassForNonVoid = ci;
        return ci;
    }

    private TypeReference GetTaskReturnType(TypeReference taskType)
    {
        TypeReference type = taskType;
        do
        {
            if (TypeEquals(type, taskBaseType))
                return taskType.Module.TypeSystem.Void;
            else if (MatchesOpenGenericType(taskOpenType, type))
                return ((GenericInstanceType)type).GenericArguments[0];
            type = type.Resolve().BaseType;
        } while (type != null);
        return null;
    }

    private bool MatchesOpenGenericType(TypeReference openType, TypeReference type)
    {
        var genType = type as GenericInstanceType;
        if (genType == null)
            return false;
        return TypeEquals(genType.ElementType, openType);
    }

    private bool IsDescendedFrom(TypeReference ancestor, TypeReference type)
    {
        do
        {
            if (TypeEquals(ancestor, type))
                return true;
            type = type.Resolve().BaseType;
        } while (type != null);
        return false;
    }

    private static bool TypeEquals(TypeReference type1, TypeReference type2)
    {
        /* This naïve approach is probably good enough here unless someone intentionally
           wants to break things by defining types with the same full names. */
        return type1.Namespace == type2.Namespace && type1.Name == type2.Name;
    }

    private class ContinuationClassInfo
    {
        public TypeReference ContinuationClass { get; set; }
        public MethodReference Constructor { get; set; }
        public FieldReference CallbackField { get; set; }
        public FieldReference TcsField { get; set; }
        public MethodReference InvokeMethod { get; set; }
    }

    private class TypeReferenceComparer: IEqualityComparer<TypeReference>
    {
        public bool Equals(TypeReference x, TypeReference y)
        {
            return TypeEquals(x, y);
        }

        public int GetHashCode(TypeReference obj)
        {
            return obj.FullName.GetHashCode();
        }
    }
}
