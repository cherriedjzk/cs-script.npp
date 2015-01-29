using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using CSScriptLibrary;
using csscript;
using ICSharpCode.NRefactory.Editor;
using System.Xml;
using System.Diagnostics;

namespace CSScriptIntellisense
{
    public static class CSScriptHelper
    {
        static string nppScriptsAsm;
        static string NppScriptsAsm
        {
            get
            {
                if (nppScriptsAsm == null)
                    nppScriptsAsm = AppDomain.CurrentDomain.GetAssemblies()
                                                           .Where(x => x.FullName.StartsWith("NppScripts,"))
                                                           .Select(x => x.Location)
                                                           .FirstOrDefault();
                return nppScriptsAsm;
            }
        }

        static public string[] GetGlobalSearchDirs()
        {
            var csscriptDir = Environment.GetEnvironmentVariable("CSSCRIPT_DIR");
            if (csscriptDir != null)
            {
                var dirs = new List<string>();
                dirs.Add(Environment.ExpandEnvironmentVariables("%CSSCRIPT_DIR%\\Lib"));

                try
                {
                    var configFile = Path.Combine(csscriptDir, "css_config.xml");

                    if (File.Exists(configFile))
                    {
                        var doc = new XmlDocument();
                        doc.Load(configFile);
                        dirs.AddRange(doc.FirstChild
                                         .SelectSingleNode("searchDirs")
                                         .InnerText.Split(';')
                                         .Select(x => Environment.ExpandEnvironmentVariables(x)));
                    }
                }
                catch { }
                return dirs.ToArray();
            }
            return new string[0];
        }

        static public List<string> RemoveEmptyAndDulicated(this List<string> collection)
        {
            collection.RemoveAll(x => string.IsNullOrEmpty(x));
            var distinct = collection.Distinct().ToArray();
            collection.Clear();
            collection.AddRange(distinct);
            return collection;
        }


        static public List<string> AgregateReferences(this ScriptParser parser, IEnumerable<string> searchDirs)
        {
            var probingDirs = searchDirs.ToArray();

            //some assemblies are referenced from code and some will need to be resolved from the namespaces
            var refNsAsms = parser.ReferencedNamespaces
                                  .Where(name => !parser.IgnoreNamespaces.Contains(name))
                                  .SelectMany(name => AssemblyResolver.FindAssembly(name, probingDirs));
            
            var refPkAsms = parser.ResolvePackages(suppressDownloading: true);

            var refCodeAsms = parser.ReferencedAssemblies
                                    .SelectMany(asm => AssemblyResolver.FindAssembly(asm.Replace("\"", ""), probingDirs));

            var refAsms = refNsAsms.Union(refPkAsms)
                                   .Union(refCodeAsms)
                                   .Distinct()
                                   .ToArray();

            refAsms = FilterDuplicatedAssembliesByFileName(refAsms);
            //refAsms = FilterDuplicatedAssembliesWithReflection(refAsms); //for possible more comprehensive filtering in future 
            return refAsms.ToList();
        }

        static public T CreateInstanceFromAndUnwrap<T>(this AppDomain domain)
        {
            Type type = typeof(T);
            return (T)domain.CreateInstanceFromAndUnwrap(type.Assembly.Location, type.ToString());
        }

        class RemoteResolver : MarshalByRefObject
        {
            //Must be done remotely to avoid loading collisions like below:
            //"Additional information: API restriction: The assembly 'file:///...\CSScriptLibrary.dll' has 
            //already loaded from a different location. It cannot be loaded from a new location within the same appdomain."
            public string[] Filter(string[] assemblies)
            {
                var uniqueAsms = new List<string>();
                var asmNames = new List<string>();
                foreach (var item in assemblies)
                {
                    try
                    {
                        string name = Assembly.ReflectionOnlyLoadFrom(item).GetName().Name;
                        if (!asmNames.Contains(name))
                        {
                            uniqueAsms.Add(item);
                            asmNames.Add(name);
                        }
                    }
                    catch { }
                }
                return uniqueAsms.ToArray();
            }
        }

        static string[] FilterDuplicatedAssembliesWithReflection(string[] assemblies)
        {
            var tempDomain = AppDomain.CurrentDomain.Clone();

            var resolver = tempDomain.CreateInstanceFromAndUnwrap<RemoteResolver>();
            var newAsms = resolver.Filter(assemblies);

            tempDomain.Unload();

            return newAsms;
        }

        static string[] FilterDuplicatedAssembliesByFileName(string[] assemblies)
        {
            var uniqueAsms = new List<string>();
            var asmNames = new List<string>();
            foreach (var item in assemblies)
            {
                try
                {
                    string name = Path.GetFileNameWithoutExtension(item);
                    if (!asmNames.Contains(name))
                    {
                        uniqueAsms.Add(item);
                        asmNames.Add(name);
                    }
                }
                catch { }
            }
            return uniqueAsms.ToArray();
        }

        static public Tuple<string[], string[]> GetProjectFiles(string script)
        {
            var searchDirs = new List<string>();
            //searchDirs.Add(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));

            var parser = new ScriptParser(script, searchDirs.ToArray(), false);

            searchDirs.AddRange(parser.SearchDirs);        //search dirs could be also defined n the script
            searchDirs.AddRange(GetGlobalSearchDirs());
            searchDirs.Add(ScriptsDir);
            searchDirs.RemoveEmptyAndDulicated();

            IList<string> sourceFiles = parser.SaveImportedScripts().ToList(); //this will also generate auto-scripts and save them
            sourceFiles.Add(script);

            //some assemblies are referenced from code and some will need to be resolved from the namespaces
            var refAsms = parser.AgregateReferences(searchDirs);

            if (NppScriptsAsm != null)
                refAsms.Add(NppScriptsAsm);

            return new Tuple<string[], string[]>(sourceFiles.ToArray(), refAsms.ToArray());
        }

        static public bool NeedsAutoclassWrapper(string text)
        {
            return Regex.Matches(text, @"\s?//css_args\s+/ac(,|\s+)").Count != 0;
        }

        static public string GenerateAutoclassWrapper(string text, ref int position)
        {
            return AutoclassGenerator.Process(text, ref position);
        }

        static public bool DecorateIfRequired(ref string text)
        {
            int dummy = 0;
            return DecorateIfRequired(ref text, ref dummy);
        }

        static public Tuple<int, int> GetDecorationInfo(string code)
        {
            int pos = code.IndexOf("///CS-Script auto-class generation");
            if (pos != -1)
            {
                var injectedLine = new ReadOnlyDocument(code).GetLineByOffset(pos);
                return new Tuple<int, int>(injectedLine.Offset, injectedLine.Length + Environment.NewLine.Length);

            }
            else
                return new Tuple<int, int>(-1, 0);
        }

        static public bool DecorateIfRequired(ref string text, ref int currentPos)
        {
            if (NeedsAutoclassWrapper(text))
            {
                text = GenerateAutoclassWrapper(text, ref currentPos);
                return true;
            }
            else
                return false;
        }

        static public string ScriptsDir
        {
            get
            {
                string rootDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string scriptDir = Path.Combine(rootDir, "NppScripts");

                if (!Directory.Exists(scriptDir))
                    Directory.CreateDirectory(scriptDir);

                return scriptDir;
            }
        }
    }
}
