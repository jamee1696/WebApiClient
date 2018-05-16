﻿using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace WebApiClient.AOT.Task
{
    /// <summary>
    /// 表示接口的代理类型
    /// </summary>
    class CeProxyType : CeMetadata
    {
        /// <summary>
        /// IApiInterceptor的Intercept方法
        /// </summary>
        private static readonly MethodInfo interceptMethod = typeof(IApiInterceptor).GetMethod("Intercept");

        /// <summary>
        /// HttpApiClient的构造器
        /// </summary>
        private static readonly ConstructorInfo baseConstructor = typeof(HttpApiClient).GetConstructor(new Type[] { typeof(IApiInterceptor) });

        /// <summary>
        /// 代理类型的构造器的参数类型
        /// </summary>
        private static readonly Type[] proxyTypeCtorArgTypes = new Type[] { typeof(IApiInterceptor), typeof(MethodInfo[]) };

        /// <summary>
        /// 接口
        /// </summary>
        private readonly CeInterface @interface;

        /// <summary>
        /// 代理类型生成器
        /// </summary>
        /// <param name="interface">接口</param>
        public CeProxyType(CeInterface @interface)
           : base(@interface.Type.Module)
        {
            this.@interface = @interface;
        }

        /// <summary>
        /// 返回代理类型是否在模块中已声明
        /// </summary>      
        /// <returns></returns>
        public bool IsDefinded()
        {
            var @namespace = this.GetProxyTypeNamespace();
            var typeName = this.@interface.Type.Name;
            var proxyTypeFullName = $"{@namespace}.{typeName}";

            return this.@interface.Type.Module.GetTypes().Any(item => item.FullName == proxyTypeFullName);
        }

        /// <summary>
        /// 返回代理类型的完整空间
        /// </summary>
        /// <returns></returns>
        private string GetProxyTypeNamespace()
        {
            var namespaces = new List<string>();
            var type = this.@interface.Type.DeclaringType;
            while (type != null)
            {
                namespaces.Add(type.Name);
                type = type.DeclaringType;
            }
            namespaces.Add("WebApiClient.AutoProxy");
            return string.Join(".", ((IEnumerable<string>)namespaces).Reverse());
        }

        /// <summary>
        /// 转换为TypeDefinition
        /// </summary>
        /// <returns></returns>
        public TypeDefinition Build()
        {
            var @namespace = this.GetProxyTypeNamespace();
            var typeName = this.@interface.Type.Name;
            var attribues = this.@interface.Type.IsPublic ? Mono.Cecil.TypeAttributes.Public : Mono.Cecil.TypeAttributes.Class;
            var baseType = this.GetTypeReference(typeof(HttpApiClient));
            var proxyType = new TypeDefinition(@namespace, typeName, attribues, baseType);
            proxyType.Interfaces.Add(new InterfaceImplementation(this.@interface.Type));

            var fieldInterceptor = this.BuildField(proxyType, "interceptor", typeof(IApiInterceptor));
            var fieldApiMethods = this.BuildField(proxyType, "apiMethods", typeof(MethodInfo[]));
            this.BuildCtor(proxyType, fieldInterceptor, fieldApiMethods);

            var apiMethos = this.@interface.GetAllApis();
            this.BuildMethods(proxyType, apiMethos, fieldInterceptor, fieldApiMethods);

            return proxyType;
        }


        /// <summary>
        /// 生成代理类型的字段
        /// </summary>
        /// <param name="proxyType">代理类型</param>
        /// <param name="fieldName">字段名称</param>
        /// <param name="type">字段类型</param>
        /// <returns></returns>
        private FieldDefinition BuildField(TypeDefinition proxyType, string fieldName, Type type)
        {
            const Mono.Cecil.FieldAttributes filedAttribute = Mono.Cecil.FieldAttributes.Private | Mono.Cecil.FieldAttributes.InitOnly; ;
            var filed = new FieldDefinition(fieldName, filedAttribute, this.GetTypeReference(type));
            proxyType.Fields.Add(filed);
            return filed;
        }

        /// <summary>
        /// 生成代理类型的构造器
        /// </summary>
        /// <param name="proxyType">代理类型</param>
        /// <param name="fieldInterceptor">拦截器字段</param>
        /// <param name="fieldApiMethods">接口方法集合字段</param>
        /// <returns></returns>
        private void BuildCtor(TypeDefinition proxyType, FieldDefinition fieldInterceptor, FieldDefinition fieldApiMethods)
        {
            // this(IApiInterceptor interceptor, MethodInfo[] methods):base(interceptor)          
            var ctor = new MethodDefinition(".ctor", Mono.Cecil.MethodAttributes.Public, this.GetTypeReference(typeof(void)))
            {
                Attributes = Mono.Cecil.MethodAttributes.Public
                | Mono.Cecil.MethodAttributes.HideBySig
                | Mono.Cecil.MethodAttributes.SpecialName
                | Mono.Cecil.MethodAttributes.RTSpecialName
            };

            foreach (var item in proxyTypeCtorArgTypes)
            {
                var parameter = new ParameterDefinition(this.GetTypeReference(item));
                ctor.Parameters.Add(parameter);
            }

            var il = ctor.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, this.GetMethodReference(baseConstructor));

            // this.interceptor = 第一个参数
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, fieldInterceptor);

            // this.apiMethods = 第二个参数
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Stfld, fieldApiMethods);

            il.Emit(OpCodes.Ret);

            proxyType.Methods.Add(ctor);
        }

        /// <summary>
        /// 生成代理类型的接口实现方法
        /// </summary>
        /// <param name="proxyType">代理类型</param>
        /// <param name="apiMethods">接口方法集合</param>
        /// <param name="fieldInterceptor">拦截器字段</param>
        /// <param name="fieldApiMethods">接口方法集合字段</param>
        private void BuildMethods(TypeDefinition proxyType, MethodDefinition[] apiMethods, FieldDefinition fieldInterceptor, FieldDefinition fieldApiMethods)
        {
            const Mono.Cecil.MethodAttributes implementAttribute = Mono.Cecil.MethodAttributes.Public
                | Mono.Cecil.MethodAttributes.Virtual
                | Mono.Cecil.MethodAttributes.Final
                | Mono.Cecil.MethodAttributes.NewSlot
                | Mono.Cecil.MethodAttributes.HideBySig;

            for (var i = 0; i < apiMethods.Length; i++)
            {
                var apiMethod = apiMethods[i];
                var apiParameters = apiMethod.Parameters;
                var parameterTypes = apiParameters.Select(p => p.ParameterType).ToArray();

                var implMethod = new MethodDefinition(apiMethod.Name, implementAttribute, apiMethod.ReturnType);
                foreach (var item in apiParameters)
                {
                    implMethod.Parameters.Add(item);
                }
                proxyType.Methods.Add(implMethod);

                var iL = implMethod.Body.GetILProcessor();

                // this.interceptor
                iL.Emit(OpCodes.Ldarg_0);
                iL.Emit(OpCodes.Ldfld, fieldInterceptor);

                // 加载target参数
                iL.Emit(OpCodes.Ldarg_0);

                // var method = this.apiMethods[i]
                iL.Create(OpCodes.Localloc);

                var method = new VariableDefinition(this.GetTypeReference(typeof(System.Reflection.MethodInfo)));
                implMethod.Body.Variables.Add(method);

                iL.Emit(OpCodes.Ldarg_0);
                iL.Emit(OpCodes.Ldfld, fieldApiMethods);
                iL.Emit(OpCodes.Ldc_I4, i);
                iL.Emit(OpCodes.Ldelem_Ref);
                iL.Emit(OpCodes.Stloc, method);

                // 加载method参数
                iL.Emit(OpCodes.Ldloc, method);

                // var parameters = new object[parameters.Length]
                var parameters = new VariableDefinition(this.GetTypeReference(typeof(object[])));
                implMethod.Body.Variables.Add(parameters);

                iL.Emit(OpCodes.Ldc_I4, apiParameters.Count);
                iL.Emit(OpCodes.Newarr, this.GetTypeReference(typeof(object)));
                iL.Emit(OpCodes.Stloc, parameters);

                for (var j = 0; j < apiParameters.Count; j++)
                {
                    iL.Emit(OpCodes.Ldloc, parameters);
                    iL.Emit(OpCodes.Ldc_I4, j);
                    iL.Emit(OpCodes.Ldarg, j + 1);

                    var parameterType = parameterTypes[j];
                    if (parameterType.IsValueType || parameterType.IsGenericParameter)
                    {
                        iL.Emit(OpCodes.Box, parameterType);
                    }
                    iL.Emit(OpCodes.Stelem_Ref);
                }

                // 加载parameters参数
                iL.Emit(OpCodes.Ldloc, parameters);

                // Intercep(this, method, parameters)
                iL.Emit(OpCodes.Callvirt, this.GetMethodReference(interceptMethod));

                iL.Emit(OpCodes.Ret);
            }
        }
    }
}