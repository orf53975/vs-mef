﻿namespace Microsoft.VisualStudio.Composition
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.ComponentModel;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading.Tasks;
    using Validation;

    partial class CompositionTemplateFactory
    {
        private const string InstantiatedPartLocalVarName = "result";

        private readonly HashSet<Assembly> relevantAssemblies = new HashSet<Assembly>();

        public CompositionConfiguration Configuration { get; set; }

        /// <summary>
        /// Gets the relevant assemblies that must be referenced when compiling the generated code.
        /// </summary>
        public ISet<Assembly> RelevantAssemblies
        {
            get { return this.relevantAssemblies; }
        }

        private IDisposable EmitMemberAssignment(Import import)
        {
            Requires.NotNull(import, "import");

            var importingField = import.ImportingMember as FieldInfo;
            var importingProperty = import.ImportingMember as PropertyInfo;
            Assumes.True(importingField != null || importingProperty != null);

            string tail;
            if (IsPublic(import.ImportingMember, import.ComposablePartType, setter: true))
            {
                this.Write("{0}.{1} = ", InstantiatedPartLocalVarName, import.ImportingMember.Name);
                tail = ";";
            }
            else
            {
                if (importingField != null)
                {
                    this.Write(
                        "{0}.SetValue({1}, ",
                        this.GetFieldInfoExpression(importingField),
                        InstantiatedPartLocalVarName);
                    tail = ");";
                }
                else // property
                {
                    this.Write(
                        "{0}.Invoke({1}, new object[] {{ ",
                        this.GetMethodInfoExpression(importingProperty.GetSetMethod(true)),
                        InstantiatedPartLocalVarName);
                    tail = " });";
                }
            }

            return new DisposableWithAction(delegate
            {
                this.WriteLine(tail);
            });
        }

        private string GetFieldInfoExpression(FieldInfo fieldInfo)
        {
            Requires.NotNull(fieldInfo, "fieldInfo");

            if (fieldInfo.DeclaringType.IsGenericType)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.GetField({1}, BindingFlags.Instance | BindingFlags.NonPublic)",
                    this.GetClosedGenericTypeExpression(fieldInfo.DeclaringType),
                    Quote(fieldInfo.Name));
            }
            else
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.ManifestModule.ResolveField({1}/*{2}*/)",
                    this.GetAssemblyExpression(fieldInfo.DeclaringType.Assembly),
                    fieldInfo.MetadataToken,
                    GetTypeName(fieldInfo.DeclaringType, evenNonPublic: true) + "." + fieldInfo.Name);
            }
        }

        private string GetMethodInfoExpression(MethodInfo methodInfo)
        {
            Requires.NotNull(methodInfo, "methodInfo");

            if (methodInfo.DeclaringType.IsGenericType)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "((MethodInfo)MethodInfo.GetMethodFromHandle({0}.ManifestModule.ResolveMethod({1}/*{3}*/).MethodHandle, {2}))",
                    this.GetAssemblyExpression(methodInfo.DeclaringType.Assembly),
                    methodInfo.MetadataToken,
                    this.GetClosedGenericTypeHandleExpression(methodInfo.DeclaringType),
                    GetTypeName(methodInfo.DeclaringType, evenNonPublic: true) + "." + methodInfo.Name);
            }
            else
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "((MethodInfo){0}.ManifestModule.ResolveMethod({1}/*{2}*/))",
                    this.GetAssemblyExpression(methodInfo.DeclaringType.Assembly),
                    methodInfo.MetadataToken,
                    GetTypeName(methodInfo.DeclaringType, evenNonPublic: true) + "." + methodInfo.Name);
            }
        }

        private string GetClosedGenericTypeExpression(Type type)
        {
            Requires.NotNull(type, "type");
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}.ManifestModule.ResolveType({1}/*{3}*/).MakeGenericType({2})",
                this.GetAssemblyExpression(type.Assembly),
                type.GetGenericTypeDefinition().MetadataToken,
                string.Join(", ", type.GetGenericArguments().Select(t => t.IsGenericType && t.ContainsGenericParameters ? GetClosedGenericTypeExpression(t) : GetTypeExpression(t))),
                type.ContainsGenericParameters ? "incomplete" : this.GetTypeName(type, evenNonPublic: true));
        }

        private string GetClosedGenericTypeHandleExpression(Type type)
        {
            return GetClosedGenericTypeExpression(type) + ".TypeHandle";
        }

        private string GetAssemblyExpression(Assembly assembly)
        {
            Requires.NotNull(assembly, "assembly");

            return string.Format(CultureInfo.InvariantCulture, "Assembly.Load({0})", Quote(assembly.FullName));
        }

        private void EmitImportSatisfyingAssignment(KeyValuePair<Import, IReadOnlyList<Export>> satisfyingExport)
        {
            Requires.Argument(satisfyingExport.Key.ImportingMember != null, "satisfyingExport", "No member to satisfy.");
            var import = satisfyingExport.Key;
            var importingMember = satisfyingExport.Key.ImportingMember;
            var exports = satisfyingExport.Value;

            var right = new StringWriter();
            EmitImportSatisfyingExpression(import, exports, right);
            string rightString = right.ToString();
            if (rightString.Length > 0)
            {
                using (this.EmitMemberAssignment(import))
                {
                    this.Write(rightString);
                }
            }
        }

        private void EmitImportSatisfyingExpression(Import import, IReadOnlyList<Export> exports, StringWriter writer)
        {
            if (import.ImportDefinition.Cardinality == ImportCardinality.ZeroOrMore)
            {
                Type enumerableOfTType = typeof(IEnumerable<>).MakeGenericType(import.ImportingSiteTypeWithoutCollection);
                if (import.ImportingSiteType.IsArray || import.ImportingSiteType.IsEquivalentTo(enumerableOfTType))
                {
                    this.EmitSatisfyImportManyArrayOrEnumerable(import, exports);
                }
                else
                {
                    this.EmitSatisfyImportManyCollection(import, exports);
                }
            }
            else if (exports.Any())
            {
                this.EmitValueFactory(import, exports.Single(), writer);
            }
        }

        private void EmitSatisfyImportManyArrayOrEnumerable(Import import, IReadOnlyList<Export> exports)
        {
            Requires.NotNull(import, "import");
            Requires.NotNull(exports, "exports");

            IDisposable memberAssignment = null;
            if (import.ImportingMember != null)
            {
                memberAssignment = this.EmitMemberAssignment(import);
            }

            this.EmitSatisfyImportManyArrayOrEnumerableExpression(import, exports);

            if (memberAssignment != null)
            {
                memberAssignment.Dispose();
            }
        }

        private void EmitSatisfyImportManyArrayOrEnumerableExpression(Import import, IEnumerable<Export> exports)
        {
            Requires.NotNull(import, "import");
            Requires.NotNull(exports, "exports");

            this.Write("new ");
            if (import.ImportingSiteType != null && import.ImportingSiteType.IsArray)
            {
                this.WriteLine("{0}[]", GetTypeName(import.ImportingSiteTypeWithoutCollection));
            }
            else
            {
                this.WriteLine("List<{0}>", GetTypeName(import.ImportingSiteTypeWithoutCollection));
            }

            this.WriteLine("{");
            using (Indent())
            {
                foreach (var export in exports)
                {
                    var valueWriter = new StringWriter();
                    EmitValueFactory(import, export, valueWriter);
                    this.WriteLine("{0},", valueWriter);
                }
            }

            this.Write("}");
        }

        private string EmitOpenGenericExportCollection(ImportDefinition importDefinition, IEnumerable<Export> exports)
        {
            Requires.NotNull(importDefinition, "importDefinition");
            Requires.NotNull(exports, "exports");

            const string localVarName = "temp";
            this.WriteLine("Array {0} = Array.CreateInstance(typeof(ILazy<>).MakeGenericType(compositionContract.Type), {1});", localVarName, exports.Count().ToString(CultureInfo.InvariantCulture));

            int index = 0;
            foreach (var export in exports)
            {
                this.WriteLine("{0}.SetValue({1}, {2});", localVarName, GetPartOrMemberLazy(export), index++);
            }

            return localVarName;
        }

        private void EmitSatisfyImportManyCollection(Import import, IReadOnlyList<Export> exports)
        {
            Requires.NotNull(import, "import");
            var importDefinition = import.ImportDefinition;
            Type elementType = import.ImportDefinition.TypeIdentity;
            Type listType = typeof(List<>).MakeGenericType(elementType);
            bool stronglyTypedCollection = IsPublic(elementType, true);
            Type icollectionType = typeof(ICollection<>).MakeGenericType(elementType);
            string importManyLocalVarTypeName = stronglyTypedCollection ? GetTypeName(icollectionType) : "object";

            // Casting the collection to ICollection<T> instead of the concrete type guarantees
            // that we'll be able to call Add(T) and Clear() on it even if the type is NonPublic
            // or its methods are explicit interface implementations.
            if (import.ImportingMember is FieldInfo)
            {
                this.WriteLine("var {0} = ({3}){1}.GetValue({2});", import.ImportingMember.Name, GetFieldInfoExpression((FieldInfo)import.ImportingMember), InstantiatedPartLocalVarName, importManyLocalVarTypeName);
            }
            else
            {
                this.WriteLine("var {0} = ({3}){1}.Invoke({2}, new object[0]);", import.ImportingMember.Name, GetMethodInfoExpression(((PropertyInfo)import.ImportingMember).GetGetMethod(true)), InstantiatedPartLocalVarName, importManyLocalVarTypeName);
            }

            this.WriteLine("if ({0} == null)", import.ImportingMember.Name);
            using (Indent(withBraces: true))
            {
                if (PartDiscovery.IsImportManyCollectionTypeCreateable(import))
                {
                    if (import.ImportingSiteType.IsAssignableFrom(listType))
                    {
                        if (stronglyTypedCollection)
                        {
                            string elementTypeName = GetTypeName(elementType);
                            this.WriteLine("{0} = new List<{1}>();", import.ImportingMember.Name, elementTypeName);
                        }
                        else
                        {
                            this.Write("{0} = ", import.ImportingMember.Name);
                            EmitConstructorInvocationExpression(typeof(List<>).MakeGenericType(elementType).GetConstructor(new Type[0]), alwaysUseReflection: true, skipCast: true).Dispose();
                            this.WriteLine(";");
                        }
                    }
                    else
                    {
                        this.Write("{0} = ({1})(", import.ImportingMember.Name, importManyLocalVarTypeName);
                        using (this.EmitConstructorInvocationExpression(import.ImportingSiteType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[0], null)))
                        {
                            // no arguments to constructor
                        }

                        this.WriteLine(");");
                    }

                    if (import.ImportingMember is FieldInfo)
                    {
                        this.WriteLine("{0}.SetValue({1}, {2});", GetFieldInfoExpression((FieldInfo)import.ImportingMember), InstantiatedPartLocalVarName, import.ImportingMember.Name);
                    }
                    else
                    {
                        this.WriteLine("{0}.Invoke({1}, new object[] {{ {2} }});", GetMethodInfoExpression(((PropertyInfo)import.ImportingMember).GetSetMethod(true)), InstantiatedPartLocalVarName, import.ImportingMember.Name);
                    }
                }
                else
                {
                    this.WriteLine(
                        "throw new InvalidOperationException(\"The {0}.{1} collection must be instantiated by the importing constructor.\");",
                        import.ComposablePartType.Name,
                        import.ImportingMember.Name);
                }
            }

            this.WriteLine("else");
            using (Indent(withBraces: true))
            {
                if (stronglyTypedCollection)
                {
                    this.WriteLine("{0}.Clear();", import.ImportingMember.Name);
                }
                else
                {
                    this.WriteLine(
                        "{0}.Invoke({1}, new object[0]);",
                        GetMethodInfoExpression(icollectionType.GetMethod("Clear")),
                        import.ImportingMember.Name);
                }
            }

            this.WriteLine(string.Empty);

            foreach (var export in exports)
            {
                var valueWriter = new StringWriter();
                EmitValueFactory(import, export, valueWriter);
                if (stronglyTypedCollection)
                {
                    this.WriteLine("{0}.Add({1});", import.ImportingMember.Name, valueWriter);
                }
                else
                {
                    this.WriteLine(
                        "{0}.Invoke({1}, new object[] {{ {2} }});",
                        GetMethodInfoExpression(icollectionType.GetMethod("Add")),
                        import.ImportingMember.Name,
                        valueWriter);
                }
            }
        }

        private void EmitValueFactory(Import import, Export export, StringWriter writer)
        {
            using (this.ValueFactoryWrapper(import, export, writer))
            {
                if (export.PartDefinition.Type.IsEquivalentTo(import.ComposablePartType) && !import.IsExportFactory)
                {
                    // The part is importing itself. So just assign it directly.
                    writer.Write(InstantiatedPartLocalVarName);
                }
                else if (export.IsStaticExport)
                {
                    if (IsPublic(export.ExportingMember, export.PartDefinition.Type))
                    {
                        writer.Write(GetTypeName(export.PartDefinition.Type));
                    }
                    else
                    {
                        // What we write here will be emitted as the argument to a reflection GetValue method call.
                        writer.Write("null");
                    }
                }
                else
                {
                    var genericTypeArgs = import.ImportDefinition.Contract.Type.GetGenericArguments();
                    string provisionalSharedObjectsExpression = import.IsExportFactory
                        ? "new Dictionary<Type, object>()"
                        : "provisionalSharedObjects";
                    bool nonSharedInstanceRequired = import.ImportDefinition.RequiredCreationPolicy == CreationPolicy.NonShared;

                    if (genericTypeArgs.All(arg => IsPublic(arg, true)))
                    {
                        writer.Write("{0}(", GetPartFactoryMethodName(export.PartDefinition, import.ImportDefinition.Contract.Type.GetGenericArguments().Select(GetTypeName).ToArray()));
                        writer.Write(provisionalSharedObjectsExpression);
                        writer.Write(", nonSharedInstanceRequired: {0})", nonSharedInstanceRequired ? "true" : "false");
                    }
                    else
                    {
                        string expression = GetGenericPartFactoryMethodInvokeExpression(
                            export.PartDefinition,
                            string.Join(", ", genericTypeArgs.Select(t => GetTypeExpression(t))),
                            provisionalSharedObjectsExpression,
                            nonSharedInstanceRequired);
                        writer.Write("((ILazy<object>)({0}))", expression);
                    }
                }
            }
        }

        private string GetExportMetadata(Export export)
        {
            var builder = new StringBuilder();
            builder.Append("new Dictionary<string, object> {");
            foreach (var metadatum in export.ExportDefinition.Metadata)
            {
                builder.AppendFormat(" {{ \"{0}\", {1} }}, ", metadatum.Key, GetExportMetadataValueExpression(metadatum.Value));
            }
            builder.Append("}.ToImmutableDictionary()");
            return builder.ToString();
        }

        private string GetExportMetadataValueExpression(object value)
        {
            if (value == null)
            {
                return "null";
            }

            Type valueType = value.GetType();
            if (value is string)
            {
                return "\"" + value + "\"";
            }
            else if (typeof(char).IsEquivalentTo(valueType))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "'{0}'",
                    (char)value == '\'' ? "\\'" : value);
            }
            else if (typeof(bool).IsEquivalentTo(valueType))
            {
                return (bool)value ? "true" : "false";
            }
            else if (valueType.IsPrimitive)
            {
                return string.Format(CultureInfo.InvariantCulture, "({0})({1})", GetTypeName(valueType), value);
            }
            else if (valueType.IsEquivalentTo(typeof(Guid)))
            {
                return string.Format(CultureInfo.InvariantCulture, "Guid.Parse(\"{0}\")", value);
            }
            else if (valueType.IsEnum)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "({0}){1}",
                    GetTypeName(valueType),
                    Convert.ChangeType(value, Enum.GetUnderlyingType(valueType)));
            }
            else if (typeof(Type).IsAssignableFrom(valueType))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "({1})typeof({0})",
                    GetTypeName((Type)value),
                    GetTypeName(valueType));
            }
            else if (valueType.IsArray)
            {
                var builder = new StringBuilder();
                builder.AppendFormat("new {0}[] {{ ", GetTypeName(valueType.GetElementType()));
                bool firstValue = true;
                foreach (object element in (Array)value)
                {
                    if (!firstValue)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(GetExportMetadataValueExpression(element));
                    firstValue = false;
                }

                builder.Append("}");
                return builder.ToString();
            }

            throw new NotSupportedException();
        }

        private IDisposable EmitConstructorInvocationExpression(ComposablePartDefinition partDefinition)
        {
            Requires.NotNull(partDefinition, "partDefinition");
            return this.EmitConstructorInvocationExpression(partDefinition.ImportingConstructorInfo);
        }

        private IDisposable EmitConstructorInvocationExpression(ConstructorInfo ctor, bool alwaysUseReflection = false, bool skipCast = false)
        {
            Requires.NotNull(ctor, "ctor");

            bool publicCtor = !alwaysUseReflection && IsPublic(ctor, ctor.DeclaringType);
            if (publicCtor)
            {
                this.Write("new {0}(", GetTypeName(ctor.DeclaringType));
            }
            else
            {
                string assemblyExpression = GetAssemblyExpression(ctor.DeclaringType.Assembly);
                string ctorExpression;
                if (ctor.DeclaringType.IsGenericType)
                {
                    ctorExpression = string.Format(
                        CultureInfo.InvariantCulture,
                        "(ConstructorInfo)MethodInfo.GetMethodFromHandle({2}.ManifestModule.ResolveMethod({0}/*{3}*/).MethodHandle, {1})",
                        ctor.MetadataToken,
                        this.GetClosedGenericTypeHandleExpression(ctor.DeclaringType),
                        GetAssemblyExpression(ctor.DeclaringType.Assembly),
                        GetTypeName(ctor.DeclaringType, evenNonPublic: true) + "." + ctor.Name);
                }
                else
                {
                    ctorExpression = string.Format(
                        CultureInfo.InvariantCulture,
                        "(ConstructorInfo){0}.ManifestModule.ResolveMethod({1}/*{2}*/)",
                        GetAssemblyExpression(ctor.DeclaringType.Assembly),
                        ctor.MetadataToken,
                        GetTypeName(ctor.DeclaringType, evenNonPublic: true) + "." + ctor.Name);
                }

                this.Write("({0})({1}).Invoke(new object[] {{", (skipCast || !IsPublic(ctor.DeclaringType, true)) ? "object" : GetTypeName(ctor.DeclaringType), ctorExpression);
            }
            var indent = this.Indent();

            return new DisposableWithAction(delegate
            {
                indent.Dispose();
                if (publicCtor)
                {
                    this.Write(")");
                }
                else
                {
                    this.Write(" })");
                }
            });
        }

        private void EmitInstantiatePart(ComposablePart part)
        {
            if (!part.Definition.IsInstantiable)
            {
                this.WriteLine("return CannotInstantiatePartWithNoImportingConstructor();");
                return;
            }

            this.Write("var {0} = ", InstantiatedPartLocalVarName);
            using (this.EmitConstructorInvocationExpression(part.Definition))
            {
                if (part.Definition.ImportingConstructor.Count > 0)
                {
                    this.WriteLine(string.Empty);
                    bool first = true;
                    foreach (var import in part.GetImportingConstructorImports())
                    {
                        if (!first)
                        {
                            this.WriteLine(",");
                        }

                        var expressionWriter = new StringWriter();
                        this.EmitImportSatisfyingExpression(import.Key, import.Value, expressionWriter);
                        this.Write(expressionWriter.ToString());
                        first = false;
                    }
                }
            }

            this.WriteLine(";");
            if (typeof(IDisposable).IsAssignableFrom(part.Definition.Type))
            {
                this.WriteLine("this.TrackDisposableValue((IDisposable){0});", InstantiatedPartLocalVarName);
            }

            this.WriteLine("provisionalSharedObjects.Add(partType, {0});", InstantiatedPartLocalVarName);

            foreach (var satisfyingExport in part.SatisfyingExports.Where(i => i.Key.ImportingMember != null))
            {
                this.EmitImportSatisfyingAssignment(satisfyingExport);
            }

            if (part.Definition.OnImportsSatisfied != null)
            {
                if (part.Definition.OnImportsSatisfied.DeclaringType.IsInterface)
                {
                    this.WriteLine("var onImportsSatisfiedInterface = ({0}){1};", part.Definition.OnImportsSatisfied.DeclaringType.FullName, InstantiatedPartLocalVarName);
                    this.WriteLine("onImportsSatisfiedInterface.{0}();", part.Definition.OnImportsSatisfied.Name);
                }
                else
                {
                    this.WriteLine("{0}.{1}();", InstantiatedPartLocalVarName, part.Definition.OnImportsSatisfied.Name);
                }
            }

            this.WriteLine("return {0};", InstantiatedPartLocalVarName);
        }

        private HashSet<Type> GetMetadataViewInterfaces()
        {
            var set = new HashSet<Type>();

            set.UnionWith(
                from part in this.Configuration.Parts
                from importAndExports in part.SatisfyingExports
                where importAndExports.Value.Count > 0
                let metadataType = importAndExports.Key.MetadataType
                where metadataType != null && metadataType.IsInterface && metadataType != typeof(IDictionary<string, object>)
                select metadataType);

            return set;
        }

        private IDisposable ValueFactoryWrapper(Import import, Export export, TextWriter writer)
        {
            var importDefinition = import.ImportDefinition;

            LazyConstructionResult closeLazy = null;
            bool closeParenthesis = false;
            if (import.IsLazyConcreteType || (export.ExportingMember != null && import.IsLazy))
            {
                if (IsPublic(importDefinition.TypeIdentity))
                {
                    string lazyTypeName = GetTypeName(LazyPart.FromLazy(import.ImportingSiteTypeWithoutCollection));
                    if (import.MetadataType == null && importDefinition.Contract.Type.IsEquivalentTo(export.PartDefinition.Type) && import.ComposablePartType != export.PartDefinition.Type)
                    {
                        writer.Write("({0})", lazyTypeName);
                    }
                    else
                    {
                        writer.Write("new {0}(() => ", lazyTypeName);
                        closeParenthesis = true;
                    }
                }
                else
                {
                    closeLazy = this.EmitLazyConstruction(importDefinition.TypeIdentity, import.MetadataType, writer);
                }
            }
            else if (import.IsExportFactory)
            {
                var exportFactoryEmitClose = this.EmitExportFactoryConstruction(import, writer);
                writer.Write("() => { var temp = ");

                if (importDefinition.ExportFactorySharingBoundaries.Count > 0)
                {
                    writer.Write("new CompiledExportProvider(this, new [] { ");
                    writer.Write(string.Join(", ", importDefinition.ExportFactorySharingBoundaries.Select(Quote)));
                    writer.Write(" }).");
                }

                return new DisposableWithAction(delegate
                {
                    writer.Write(".Value; return ");
                    using (this.EmitExportFactoryTupleConstruction(importDefinition.TypeIdentity, "temp", writer))
                    {
                        writer.Write("() => { ");
                        if (typeof(IDisposable).IsAssignableFrom(export.PartDefinition.Type))
                        {
                            writer.Write("((IDisposable)temp).Dispose(); ");
                        }

                        writer.Write("}");
                    }

                    writer.Write("; }");
                    this.WriteExportMetadataReference(export, import, writer);
                    exportFactoryEmitClose.Dispose();
                });
            }
            else if (!IsPublic(export.PartDefinition.Type) && IsPublic(import.ImportingSiteTypeWithoutCollection, true))
            {
                writer.Write("({0})", GetTypeName(import.ImportingSiteTypeWithoutCollection));
            }

            if (export.ExportingMember != null && !IsPublic(export.ExportingMember, export.PartDefinition.Type))
            {
                closeParenthesis = true;

                switch (export.ExportingMember.MemberType)
                {
                    case MemberTypes.Field:
                        writer.Write(
                            "({0}){1}.GetValue(",
                            GetTypeName(import.ImportDefinition.Contract.Type),
                            GetFieldInfoExpression((FieldInfo)export.ExportingMember));
                        break;
                    case MemberTypes.Method:
                        writer.Write(
                            "({0}){1}.CreateDelegate({2}, ",
                            GetTypeName(import.ImportDefinition.Contract.Type),
                            GetMethodInfoExpression((MethodInfo)export.ExportingMember),
                            GetTypeExpression(import.ImportDefinition.TypeIdentity));
                        break;
                    case MemberTypes.Property:
                        writer.Write(
                            "({0}){1}.Invoke(",
                            GetTypeName(import.ImportDefinition.Contract.Type),
                            GetMethodInfoExpression(((PropertyInfo)export.ExportingMember).GetGetMethod(true)));
                        break;
                    default:
                        throw new NotSupportedException();
                }
            }

            return new DisposableWithAction(() =>
            {
                string memberModifier = string.Empty;
                if (export.ExportingMember != null)
                {
                    if (IsPublic(export.ExportingMember, export.PartDefinition.Type))
                    {
                        memberModifier = "." + export.ExportingMember.Name;
                    }
                    else
                    {
                        switch (export.ExportingMember.MemberType)
                        {
                            case MemberTypes.Field:
                                memberModifier = ")";
                                break;
                            case MemberTypes.Property:
                                memberModifier = ", new object[0])";
                                break;
                            case MemberTypes.Method:
                                memberModifier = ")";
                                break;
                        }
                    }
                }

                string memberAccessor = memberModifier;
                if ((export.PartDefinition.Type != import.ComposablePartType || import.IsExportFactory) && !export.IsStaticExport)
                {
                    memberAccessor = ".Value" + memberAccessor;
                }

                if (import.IsLazy)
                {
                    if (import.MetadataType != null)
                    {
                        writer.Write(memberAccessor);
                        if (closeLazy != null)
                        {
                            closeLazy.OnBeforeWriteMetadata();
                        }

                        this.WriteExportMetadataReference(export, import, writer);
                    }
                    else if (import.IsLazyConcreteType && !importDefinition.Contract.Type.IsEquivalentTo(export.PartDefinition.Type))
                    {
                        writer.Write(memberAccessor);
                    }
                    else if (closeLazy != null || (export.ExportingMember != null && import.IsLazy))
                    {
                        writer.Write(memberAccessor);
                    }

                    if (closeLazy != null)
                    {
                        closeLazy.Dispose();
                    }
                    else if (closeParenthesis)
                    {
                        writer.Write(")");
                    }
                }
                else if (import.ComposablePartType != export.PartDefinition.Type)
                {
                    writer.Write(memberAccessor);
                }
            });
        }

        private void EmitGetExportsReturnExpression(CompositionContract contract, IEnumerable<Export> exports)
        {
            using (Indent(4))
            {
                if (contract.Type.IsGenericTypeDefinition)
                {
                    string localVarName = this.EmitOpenGenericExportCollection(WrapContractAsImportDefinition(contract), exports);
                    this.WriteLine("return (IEnumerable<object>){0};", localVarName);
                }
                else
                {
                    this.Write("return ");
                    this.EmitSatisfyImportManyArrayOrEnumerableExpression(WrapContractAsImport(contract), exports);
                    this.WriteLine(";");
                }
            }
        }

        private void WriteExportMetadataReference(Export export, Import import, TextWriter writer)
        {
            if (import.MetadataType != null)
            {
                writer.Write(", ");
                if (import.MetadataType != typeof(IDictionary<string, object>))
                {
                    writer.Write("new {0}(", GetClassNameForMetadataView(import.MetadataType));
                }

                writer.Write(GetExportMetadata(export));
                if (import.MetadataType != typeof(IDictionary<string, object>))
                {
                    writer.Write(")");
                }
            }
        }

        private IEnumerable<IGrouping<string, IGrouping<CompositionContract, Export>>> ExportsByContract
        {
            get
            {
                return
                    from part in this.Configuration.Parts
                    from exportingMemberAndDefinition in part.Definition.ExportDefinitions
                    let export = new Export(exportingMemberAndDefinition.Value, part.Definition, exportingMemberAndDefinition.Key)
                    where part.Definition.IsInstantiable || part.Definition.Equals(ExportProvider.ExportProviderPartDefinition) // normally they must be instantiable, but we have one special case.
                    group export by export.ExportDefinition.Contract into exportsByContract
                    group exportsByContract by exportsByContract.Key.ContractName into exportsByContractByName
                    select exportsByContractByName;
            }
        }

        private string GetTypeName(Type type)
        {
            return this.GetTypeName(type, false, false);
        }

        private string GetTypeName(Type type, bool genericTypeDefinition = false, bool evenNonPublic = false)
        {
            return ReflectionHelpers.GetTypeName(type, genericTypeDefinition, evenNonPublic, this.relevantAssemblies);
        }

        /// <summary>
        /// Gets a C# expression that evaluates to a System.Type instance for the specified type.
        /// </summary>
        private string GetTypeExpression(Type type, bool genericTypeDefinition = false)
        {
            Requires.NotNull(type, "type");

            if (IsPublic(type, true))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "typeof({0})",
                    this.GetTypeName(type, genericTypeDefinition));
            }
            else
            {
                var targetType = (genericTypeDefinition && type.IsGenericType) ? type.GetGenericTypeDefinition() : type;
                var expression = new StringBuilder();
                expression.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "Type.GetType({0})",
                    Quote(targetType.AssemblyQualifiedName));
                if (!genericTypeDefinition && targetType.IsGenericTypeDefinition)
                {
                    // Concatenate on the generic type arguments if the caller didn't explicitly want the generic type definition.
                    // Note that the type itself may be a generic type definition, in which case the concatenated types might be
                    // T1, T2. That's fine. In fact that's what we want because it causes the types from the caller's caller to
                    // propagate.
                    expression.Append(".MakeGenericType(");
                    foreach (Type typeArg in targetType.GetGenericArguments())
                    {
                        expression.Append(this.GetTypeExpression(typeArg, false));
                        expression.Append(", ");
                    }

                    expression.Length -= 2;
                    expression.Append(")");
                }

                return expression.ToString();
            }
        }

        private static bool IsPublic(Type type, bool checkGenericTypeArgs = false)
        {
            return ReflectionHelpers.IsPublic(type, checkGenericTypeArgs);
        }

        private string GetClassNameForMetadataView(Type metadataView)
        {
            Requires.NotNull(metadataView, "metadataView");

            if (metadataView.IsInterface)
            {
                return "ClassFor" + metadataView.Name;
            }

            return this.GetTypeName(metadataView);
        }

        private string GetValueOrDefaultForMetadataView(PropertyInfo property, string sourceVarName)
        {
            var defaultValueAttribute = property.GetCustomAttribute<DefaultValueAttribute>();
            if (defaultValueAttribute != null)
            {
                return String.Format(
                    CultureInfo.InvariantCulture,
                    @"({0})({1}.ContainsKey(""{2}"") ? {1}[""{2}""]{4} : {3})",
                    this.GetTypeName(property.PropertyType),
                    sourceVarName,
                    property.Name,
                    GetExportMetadataValueExpression(defaultValueAttribute.Value),
                    property.PropertyType.IsValueType ? string.Empty : (" as " + this.GetTypeName(property.PropertyType)));
            }
            else
            {
                return String.Format(
                    CultureInfo.InvariantCulture,
                    @"({0}){1}[""{2}""]",
                    this.GetTypeName(property.PropertyType),
                    sourceVarName,
                    property.Name);
            }
        }

        private static void Test<T>() { }

        private static string GetPartFactoryMethodNameNoTypeArgs(ComposablePartDefinition part)
        {
            string name = GetPartFactoryMethodName(part);
            int indexOfTypeArgs = name.IndexOf('<');
            if (indexOfTypeArgs >= 0)
            {
                return name.Substring(0, indexOfTypeArgs);
            }

            return name;
        }

        private static string GetPartFactoryMethodName(ComposablePartDefinition part, params string[] typeArguments)
        {
            if (typeArguments == null || typeArguments.Length == 0)
            {
                typeArguments = part.Type.GetGenericArguments().Select(t => t.Name).ToArray();
            }

            string name = "GetOrCreate" + ReflectionHelpers.ReplaceBackTickWithTypeArgs(part.Id, typeArguments);
            return name;
        }

        private static string GetPartFactoryMethodInvokeExpression(
            ComposablePartDefinition part,
            string typeArgsParamsArrayExpression,
            string provisionalSharedObjectsExpression,
            bool nonSharedInstanceRequired)
        {
            if (part.Type.IsGenericType)
            {
                return GetGenericPartFactoryMethodInvokeExpression(
                    part,
                    typeArgsParamsArrayExpression,
                    provisionalSharedObjectsExpression,
                    false);
            }
            else
            {
                return "this." + GetPartFactoryMethodName(part) + "(" + provisionalSharedObjectsExpression + ", " + (nonSharedInstanceRequired ? "true" : "false") + ")";
            }
        }

        private static string GetGenericPartFactoryMethodInfoExpression(ComposablePartDefinition part, string typeArgsParamsArrayExpression)
        {
            return "typeof(CompiledExportProvider).GetMethod(\"" + GetPartFactoryMethodNameNoTypeArgs(part) + "\", BindingFlags.Instance | BindingFlags.NonPublic)"
                + ".MakeGenericMethod(" + typeArgsParamsArrayExpression + ")";
        }

        private static string GetGenericPartFactoryMethodInvokeExpression(
            ComposablePartDefinition part,
            string typeArgsParamsArrayExpression,
            string provisionalSharedObjectsExpression,
            bool nonSharedInstanceRequired)
        {
            return GetGenericPartFactoryMethodInfoExpression(part, typeArgsParamsArrayExpression) +
                ".Invoke(this, new object[] { " + provisionalSharedObjectsExpression + ", /* nonSharedInstanceRequired: */ " + (nonSharedInstanceRequired ? "true" : "false") + " })";
        }

        private string GetPartOrMemberLazy(Export export)
        {
            Requires.NotNull(export, "export");

            MemberInfo member = export.ExportingMember;
            ExportDefinition exportDefinition = export.ExportDefinition;

            string partExpression = GetPartFactoryMethodInvokeExpression(
                export.PartDefinition,
                "compositionContract.Type.GetGenericArguments()",
                "provisionalSharedObjects",
                false);

            if (member == null)
            {
                return partExpression;
            }

            string valueFactoryExpression;
            if (IsPublic(member, export.PartDefinition.Type))
            {
                string memberExpression = string.Format(
                    CultureInfo.InvariantCulture,
                    "({0}).Value.{1}",
                    partExpression,
                    member.Name);
                switch (member.MemberType)
                {
                    case MemberTypes.Method:
                        valueFactoryExpression = string.Format(
                            CultureInfo.InvariantCulture,
                            "new {0}({1})",
                            GetTypeName(exportDefinition.Contract.Type),
                            memberExpression);
                        break;
                    case MemberTypes.Field:
                    case MemberTypes.Property:
                        valueFactoryExpression = memberExpression;
                        break;
                    default:
                        throw new NotSupportedException();
                }
            }
            else
            {
                switch (member.MemberType)
                {
                    case MemberTypes.Method:
                        valueFactoryExpression = string.Format(
                            CultureInfo.InvariantCulture,
                            "({0}){1}.CreateDelegate({3}, ({2}).Value)",
                            GetTypeName(exportDefinition.Contract.Type),
                            GetMethodInfoExpression((MethodInfo)member),
                            partExpression,
                            GetTypeExpression(exportDefinition.Contract.Type));
                        break;
                    case MemberTypes.Field:
                        valueFactoryExpression = string.Format(
                            CultureInfo.InvariantCulture,
                            "({0}){1}.GetValue(({2}).Value)",
                            GetTypeName(((FieldInfo)member).FieldType),
                            GetFieldInfoExpression((FieldInfo)member),
                            partExpression);
                        break;
                    case MemberTypes.Property:
                        valueFactoryExpression = string.Format(
                            CultureInfo.InvariantCulture,
                            "({0}){1}.Invoke(({2}).Value, new object[0])",
                            GetTypeName(((PropertyInfo)member).PropertyType),
                            GetMethodInfoExpression(((PropertyInfo)member).GetGetMethod(true)),
                            partExpression);
                        break;
                    default:
                        throw new NotSupportedException();
                }
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "new LazyPart<{0}>(() => {1})",
                GetTypeName(exportDefinition.Contract.Type),
                valueFactoryExpression);
        }

        private static string Quote(string value)
        {
            return "@\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static bool IsPublic(MemberInfo memberInfo, Type reflectedType, bool setter = false)
        {
            Requires.NotNull(memberInfo, "memberInfo");
            Requires.NotNull(reflectedType, "reflectedType");
            Requires.Argument(memberInfo.ReflectedType.IsAssignableFrom(reflectedType), "reflectedType", "Type must be the one that defines memberInfo or a derived type.");

            if (!IsPublic(reflectedType, true))
            {
                return false;
            }

            switch (memberInfo.MemberType)
            {
                case MemberTypes.Constructor:
                    return ((ConstructorInfo)memberInfo).IsPublic;
                case MemberTypes.Field:
                    return ((FieldInfo)memberInfo).IsPublic;
                case MemberTypes.Method:
                    return ((MethodInfo)memberInfo).IsPublic;
                case MemberTypes.Property:
                    var property = (PropertyInfo)memberInfo;
                    var method = setter ? property.GetSetMethod(true) : property.GetGetMethod(true);
                    return IsPublic(method, reflectedType);
                default:
                    throw new NotSupportedException();
            }
        }

        private static Import WrapContractAsImport(CompositionContract contract)
        {
            Requires.NotNull(contract, "contract");

            var importDefinition = WrapContractAsImportDefinition(contract);
            return new Import(importDefinition);
        }

        private static ImportDefinition WrapContractAsImportDefinition(CompositionContract contract)
        {
            Requires.NotNull(contract, "contract");

            var importDefinition = new ImportDefinition(
                contract,
                ImportCardinality.ZeroOrMore,
                ImmutableList.Create<IImportSatisfiabilityConstraint>(),
                CreationPolicy.Any);
            return importDefinition;
        }

        private LazyConstructionResult EmitLazyConstruction(Type valueType, Type metadataType, TextWriter writer = null)
        {
            writer = writer ?? new SelfTextWriter(this);
            if (IsPublic(valueType, true) && (metadataType == null || IsPublic(metadataType, true)))
            {
                writer.Write("new LazyPart<{0}", GetTypeName(valueType));
                if (metadataType != null)
                {
                    writer.Write(", {0}", GetTypeName(metadataType));
                }

                writer.Write(">(() => ");
                return new LazyConstructionResult(delegate
                {
                    writer.Write(")");
                });
            }
            else
            {
                Type lazyType = metadataType != null ? typeof(LazyPart<,>) : typeof(LazyPart<>);
                Type[] lazyTypeArgs = metadataType != null ? new[] { valueType, metadataType } : new[] { valueType };
                var ctor = lazyType.GetConstructors().Single(c => c.GetParameters()[0].ParameterType.Equals(typeof(Func<object>)));
                writer.WriteLine(
                    "((ILazy<{4}>)((ConstructorInfo)MethodInfo.GetMethodFromHandle({0}.ManifestModule.ResolveMethod({1}/*{3}*/).MethodHandle, {2})).Invoke(new object[] {{ (Func<object>)(() => ",
                    GetAssemblyExpression(lazyType.Assembly),
                    ctor.MetadataToken,
                    this.GetClosedGenericTypeHandleExpression(lazyType.MakeGenericType(lazyTypeArgs)),
                    GetTypeName(ctor.DeclaringType, evenNonPublic: true) + "." + ctor.Name,
                    GetTypeName(valueType) + (metadataType != null ? (", " + GetTypeName(metadataType)) : ""));
                var indent = Indent();
                return new LazyConstructionResult(
                    () =>
                    {
                        writer.Write(")");
                    },
                    () =>
                    {
                        indent.Dispose();
                        writer.Write(" }))");
                    });
            }
        }

        private IDisposable EmitExportFactoryConstruction(Import exportFactoryImport, TextWriter writer = null)
        {
            writer = writer ?? new SelfTextWriter(this);

            if (IsPublic(exportFactoryImport.ImportDefinition.TypeIdentity))
            {
                writer.Write("new {0}(", GetTypeName(exportFactoryImport.ExportFactoryType));
                return new DisposableWithAction(delegate
                {
                    writer.Write(")");
                });
            }
            else
            {
                var ctor = exportFactoryImport.ExportFactoryType.GetConstructors().Single();
                writer.WriteLine(
                    "((ConstructorInfo)MethodInfo.GetMethodFromHandle({0}.ManifestModule.ResolveMethod({1}/*{3}*/).MethodHandle, {2})).Invoke(new object[] {{ ",
                    GetAssemblyExpression(exportFactoryImport.ExportFactoryType.Assembly),
                    ctor.MetadataToken,
                    this.GetClosedGenericTypeHandleExpression(exportFactoryImport.ExportFactoryType),
                    ReflectionHelpers.GetTypeName(ctor.DeclaringType, false, true, null) + "." + ctor.Name);
                using (Indent())
                {
                    writer.WriteLine(
                        "{0}.CreateFuncOfType(",
                        typeof(ReflectionHelpers).FullName);
                    using (Indent())
                    {
                        writer.WriteLine(
                            "{0},",
                            this.GetExportFactoryTupleTypeExpression(exportFactoryImport.ImportDefinition.TypeIdentity));
                    }
                }

                var indent = Indent(3);
                return new DisposableWithAction(delegate
                {
                    indent.Dispose();
                    writer.WriteLine();
                    writer.Write(") })");
                });
            }
        }

        private IDisposable EmitExportFactoryTupleConstruction(Type firstArgType, string valueExpression, TextWriter writer)
        {
            if (IsPublic(firstArgType))
            {
                writer.Write(
                    "Tuple.Create<{0}, Action>(({0})({1}), ",
                    this.GetTypeName(firstArgType),
                    valueExpression);

                return new DisposableWithAction(delegate
                {
                    writer.Write(")");
                });
            }
            else
            {
                string create = GetMethodInfoExpression(
                    new Func<object, object, Tuple<object, object>>(Tuple.Create<object, object>)
                    .GetMethodInfo().GetGenericMethodDefinition());
                writer.Write(
                    "{0}.MakeGenericMethod({2}, typeof(Action)).Invoke(null, new object[] {{ {1}, (Action)(",
                    create,
                    valueExpression,
                    GetTypeExpression(firstArgType));

                return new DisposableWithAction(delegate
                {
                    writer.Write(") })");
                });
            }
        }

        private string GetExportFactoryTupleTypeExpression(Type constructedType)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "typeof(Tuple<,>).MakeGenericType({0}, typeof(System.Action))",
                this.GetTypeExpression(constructedType));
        }

        private IDisposable Indent(int count = 1, bool withBraces = false)
        {
            if (withBraces)
            {
                this.WriteLine("{");
            }

            this.PushIndent(new string(' ', count * 4));

            return new DisposableWithAction(delegate
            {
                this.PopIndent();
                if (withBraces)
                {
                    this.WriteLine("}");
                }
            });
        }

        private class SelfTextWriter : TextWriter
        {
            private CompositionTemplateFactory factory;

            internal SelfTextWriter(CompositionTemplateFactory factory)
            {
                this.factory = factory;
            }

            public override Encoding Encoding
            {
                get { return Encoding.Default; }
            }

            public override void Write(char value)
            {
                this.factory.Write(value.ToString());
            }

            public override void Write(string value)
            {
                this.factory.Write(value);
            }
        }

        private class LazyConstructionResult : IDisposable
        {
            private Action beforeWriteMetadata;
            private Action disposal;

            internal LazyConstructionResult(Action beforeWriteMetadata, Action disposal)
            {
                this.beforeWriteMetadata = beforeWriteMetadata;
                this.disposal = disposal;
            }

            internal LazyConstructionResult(Action disposal)
            {
                this.disposal = disposal;
            }

            internal void OnBeforeWriteMetadata()
            {
                if (this.beforeWriteMetadata != null)
                {
                    this.beforeWriteMetadata();
                    this.beforeWriteMetadata = null;
                }
            }

            public void Dispose()
            {
                this.OnBeforeWriteMetadata(); // in case the caller didn't bother with metadata.
                if (this.disposal != null)
                {
                    this.disposal();
                }
            }
        }
    }
}
