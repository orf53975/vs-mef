﻿// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.VisualStudio.Composition.Reflection
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Threading.Tasks;

    public static class ResolverExtensions
    {
        private const BindingFlags AllInstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static Type Resolve(this TypeRef typeRef)
        {
            return typeRef?.ResolvedType;
        }

        public static ConstructorInfo Resolve(this ConstructorRef constructorRef)
        {
            if (constructorRef.IsEmpty)
            {
                return null;
            }

#if RuntimeHandles
            var manifest = constructorRef.Resolver.GetManifest(constructorRef.DeclaringType.AssemblyName);
            return (ConstructorInfo)manifest.ResolveMethod(constructorRef.MetadataToken);
#else
            return FindMethodByParameters(
                Resolve(constructorRef.DeclaringType).GetConstructors(AllInstanceMembers),
                ConstructorInfo.ConstructorName,
                constructorRef.ParameterTypes);
#endif
        }

        [Obsolete("Use Resolve2 instead.", error: true)]
        public static MethodInfo Resolve(this MethodRef methodRef) => (MethodInfo)Resolve2(methodRef);

        public static MethodBase Resolve2(this MethodRef methodRef)
        {
            if (methodRef.IsEmpty)
            {
                return null;
            }

#if RuntimeHandles
            var manifest = methodRef.Resolver.GetManifest(methodRef.DeclaringType.AssemblyName);
            var method = manifest.ResolveMethod(methodRef.MetadataToken);
#else
            var method = FindMethodByParameters(
                Resolve(methodRef.DeclaringType).GetTypeInfo().GetMethods(AllInstanceMembers),
                methodRef.Name,
                methodRef.ParameterTypes);
#endif
            if (methodRef.GenericMethodArguments.Length > 0)
            {
                var constructedMethod = ((MethodInfo)method).MakeGenericMethod(methodRef.GenericMethodArguments.Select(Resolve).ToArray());
                return constructedMethod;
            }

            return method;
        }

        public static PropertyInfo Resolve(this PropertyRef propertyRef)
        {
            if (propertyRef.IsEmpty)
            {
                return null;
            }

            Type type = propertyRef.DeclaringType.Resolve();
#if RuntimeHandles
            return type.GetRuntimeProperties().First(p => p.MetadataToken == propertyRef.MetadataToken);
#else
            return type.GetProperty(propertyRef.Name, AllInstanceMembers);
#endif
        }

        public static MethodInfo ResolveGetter(this PropertyRef propertyRef)
        {
            if (propertyRef.GetMethodMetadataToken.HasValue)
            {
#if RuntimeHandles
                Module manifest = propertyRef.Resolver.GetManifest(propertyRef.DeclaringType.AssemblyName);
                return (MethodInfo)manifest.ResolveMethod(propertyRef.GetMethodMetadataToken.Value);
#else
                return propertyRef.PropertyInfo.GetMethod;
#endif
            }

            return null;
        }

        public static MethodInfo ResolveSetter(this PropertyRef propertyRef)
        {
            if (propertyRef.SetMethodMetadataToken.HasValue)
            {
#if RuntimeHandles
                Module manifest = propertyRef.Resolver.GetManifest(propertyRef.DeclaringType.AssemblyName);
                return (MethodInfo)manifest.ResolveMethod(propertyRef.SetMethodMetadataToken.Value);
#else
                return propertyRef.PropertyInfo.SetMethod;
#endif
            }

            return null;
        }

        public static FieldInfo Resolve(this FieldRef fieldRef)
        {
            if (fieldRef.IsEmpty)
            {
                return null;
            }

#if RuntimeHandles
            var manifest = fieldRef.Resolver.GetManifest(fieldRef.AssemblyName);
            return manifest.ResolveField(fieldRef.MetadataToken);
#else
            return Resolve(fieldRef.DeclaringType).GetField(fieldRef.Name, AllInstanceMembers);
#endif
        }

        public static ParameterInfo Resolve(this ParameterRef parameterRef)
        {
            if (parameterRef.IsEmpty)
            {
                return null;
            }

#if RuntimeHandles
            Module manifest = parameterRef.Resolver.GetManifest(parameterRef.AssemblyName);
            MethodBase method = manifest.ResolveMethod(parameterRef.Constructor.IsEmpty ? parameterRef.Method.MetadataToken : parameterRef.Constructor.MetadataToken);
#else
            MethodBase method = (MethodBase)parameterRef.Constructor.ConstructorInfo ?? parameterRef.Method.MethodBase;
#endif
            return method.GetParameters()[parameterRef.ParameterIndex];
        }

        public static MemberInfo Resolve(this MemberRef memberRef)
        {
            if (memberRef.IsEmpty)
            {
                return null;
            }

            if (memberRef.IsField)
            {
                return memberRef.Field.FieldInfo;
            }

            if (memberRef.IsProperty)
            {
                return memberRef.Property.PropertyInfo;
            }

            if (memberRef.IsMethod)
            {
                return memberRef.Method.MethodBase;
            }

            if (memberRef.IsConstructor)
            {
                return memberRef.Constructor.ConstructorInfo;
            }

            if (memberRef.IsType)
            {
                return memberRef.Type.Resolve().GetTypeInfo();
            }

            throw new NotSupportedException();
        }

        [Obsolete("Use " + nameof(MemberRef) + " instead.", error: true)]
        public static MemberInfo Resolve(this MemberDesc memberDesc)
        {
            throw new NotSupportedException();
        }

        internal static void GetInputAssemblies(this TypeRef typeRef, ISet<AssemblyName> assemblies)
        {
            Requires.NotNull(assemblies, nameof(assemblies));

            if (typeRef != null)
            {
                assemblies.Add(typeRef.AssemblyName);
                foreach (var typeArg in typeRef.GenericTypeArguments)
                {
                    GetInputAssemblies(typeArg, assemblies);
                }

                // Base types may define [InheritedExport] attributes or otherwise influence MEF
                // so we should include them as input assemblies.
                // Resolving a TypeRef is a necessary cost in order to identify the transitive closure of base types.
                var type = typeRef.Resolve();
                foreach (var baseType in type.EnumTypeAndBaseTypes())
                {
                    assemblies.Add(baseType.GetTypeInfo().Assembly.GetName());
                }

                // Interfaces may also define [InheritedExport] attributes, metadata view filters, etc.
                foreach (var iface in type.GetTypeInfo().GetInterfaces())
                {
                    assemblies.Add(iface.GetTypeInfo().Assembly.GetName());
                }
            }
        }

        internal static void GetInputAssemblies(this MemberRef memberRef, ISet<AssemblyName> assemblies)
        {
            Requires.NotNull(assemblies, nameof(assemblies));

            if (memberRef.IsConstructor)
            {
                GetInputAssemblies(memberRef.Constructor, assemblies);
            }
            else if (memberRef.IsField)
            {
                GetInputAssemblies(memberRef.Field, assemblies);
            }
            else if (memberRef.IsMethod)
            {
                GetInputAssemblies(memberRef.Method, assemblies);
            }
            else if (memberRef.IsProperty)
            {
                GetInputAssemblies(memberRef.Property, assemblies);
            }
        }

        internal static void GetInputAssemblies(this MethodRef methodRef, ISet<AssemblyName> assemblies)
        {
            Requires.NotNull(assemblies, nameof(assemblies));

            if (!methodRef.IsEmpty)
            {
                assemblies.Add(methodRef.DeclaringType.AssemblyName);
                foreach (var typeArg in methodRef.GenericMethodArguments)
                {
                    GetInputAssemblies(typeArg, assemblies);
                }
            }
        }

        internal static void GetInputAssemblies(this PropertyRef propertyRef, ISet<AssemblyName> assemblies)
        {
            Requires.NotNull(assemblies, nameof(assemblies));

            if (!propertyRef.IsEmpty)
            {
                propertyRef.DeclaringType.GetInputAssemblies(assemblies);
            }
        }

        internal static void GetInputAssemblies(this FieldRef fieldRef, ISet<AssemblyName> assemblies)
        {
            Requires.NotNull(assemblies, nameof(assemblies));

            if (!fieldRef.IsEmpty)
            {
                fieldRef.DeclaringType.GetInputAssemblies(assemblies);
            }
        }

        internal static void GetInputAssemblies(this ConstructorRef constructorRef, ISet<AssemblyName> assemblies)
        {
            Requires.NotNull(assemblies, nameof(assemblies));

            if (!constructorRef.IsEmpty)
            {
                constructorRef.DeclaringType.GetInputAssemblies(assemblies);
            }
        }

        internal static void GetInputAssemblies(this ParameterRef parameterRef, ISet<AssemblyName> assemblies)
        {
            Requires.NotNull(assemblies, nameof(assemblies));

            if (!parameterRef.IsEmpty)
            {
                parameterRef.DeclaringType.GetInputAssemblies(assemblies);
            }
        }

        internal static Module GetManifest(this Resolver resolver, AssemblyName assemblyName)
        {
            return resolver.AssemblyLoader.LoadAssembly(assemblyName).ManifestModule;
        }

        private static T FindMethodByParameters<T>(IEnumerable<T> members, string memberName, ImmutableArray<TypeRef> parameterTypes)
            where T : MethodBase
        {
            Requires.NotNull(members, nameof(members));

            foreach (var member in members)
            {
                if (member.Name != memberName)
                {
                    continue;
                }

                var parameters = member.GetParameters();
                if (parameters.Length != parameterTypes.Length)
                {
                    continue;
                }

                for (int i = 0; i < parameters.Length; i++)
                {
                    if (!parameterTypes[i].Equals(parameters[i].ParameterType))
                    {
                        continue;
                    }
                }

                return member;
            }

            return default(T);
        }
    }
}
