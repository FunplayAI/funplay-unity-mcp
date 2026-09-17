// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Funplay.Editor.Tools.Helpers
{
    internal static class ProjectTypeCatalog
    {
        private static Type[] types;
        private static int assemblyCount;
        internal static IEnumerable<Type> Types
        {
            get
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(x => !x.IsDynamic).ToArray();
                if (types != null && assemblyCount == assemblies.Length) return types;
                var all = new List<Type>();
                foreach (var assembly in assemblies)
                {
                    try { all.AddRange(assembly.GetTypes()); }
                    catch (ReflectionTypeLoadException ex) { all.AddRange(ex.Types.Where(x => x != null)); }
                    catch (NotSupportedException) { }
                }
                types = all.Where(x => x.FullName != null).OrderBy(x => x.FullName, StringComparer.Ordinal).ThenBy(x => x.Assembly.FullName, StringComparer.Ordinal).ToArray();
                assemblyCount = assemblies.Length;
                return types;
            }
        }
        internal static Type[] Candidates(string name, string assembly = null, bool componentsOnly = false) => Types.Where(x =>
            (!componentsOnly || typeof(Component).IsAssignableFrom(x)) &&
            (string.IsNullOrEmpty(assembly) || x.Assembly.GetName().Name == assembly) &&
            (string.Equals(x.FullName, name, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) || x.AssemblyQualifiedName == name)).ToArray();
        internal static object Describe(Type type) => new
        {
            name = type.Name, full_name = type.FullName, assembly = type.Assembly.GetName().Name,
            assembly_qualified_name = type.AssemblyQualifiedName, is_component = typeof(Component).IsAssignableFrom(type),
            is_unity_object = typeof(UnityEngine.Object).IsAssignableFrom(type), is_abstract = type.IsAbstract,
            is_generic = type.ContainsGenericParameters, base_type = type.BaseType?.FullName
        };
    }
}
