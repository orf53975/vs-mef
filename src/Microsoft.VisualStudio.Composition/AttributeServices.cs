﻿// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.VisualStudio.Composition
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading.Tasks;

    internal static class AttributeServices
    {
        public static T[] GetAttributes<T>(this ICustomAttributeProvider attributeProvider)
            where T : class
        {
            return (T[])attributeProvider.GetCustomAttributes(typeof(T), false);
        }

        public static T[] GetAttributes<T>(this ICustomAttributeProvider attributeProvider, bool inherit)
            where T : class
        {
            return (T[])attributeProvider.GetCustomAttributes(typeof(T), inherit);
        }

        public static T GetFirstAttribute<T>(this ICustomAttributeProvider attributeProvider)
            where T : class
        {
            return GetAttributes<T>(attributeProvider).FirstOrDefault();
        }

        public static T GetFirstAttribute<T>(this ICustomAttributeProvider attributeProvider, bool inherit)
            where T : class
        {
            return GetAttributes<T>(attributeProvider, inherit).FirstOrDefault();
        }

        public static bool IsAttributeDefined<T>(this ICustomAttributeProvider attributeProvider)
            where T : class
        {
            return attributeProvider.IsDefined(typeof(T), false);
        }

        public static bool IsAttributeDefined<T>(this ICustomAttributeProvider attributeProvider, bool inherit)
            where T : class
        {
            return attributeProvider.IsDefined(typeof(T), inherit);
        }
    }
}
