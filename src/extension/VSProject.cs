// ***********************************************************************
// Copyright (c) 2002-2014 Charlie Poole
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// ***********************************************************************

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Text.RegularExpressions;
using NUnit.Engine.Extensibility;

namespace NUnit.Engine.Services.ProjectLoaders
{
    /// <summary>
    /// This class allows loading information about
    /// configurations and assemblies in a Visual
    /// Studio project file and inspecting them.
    /// Only the most common project types are
    /// supported and an exception is thrown if
    /// an attempt is made to load an invalid
    /// file or one of an unknown type.
    /// </summary>
    public class VSProject : IProject
    {
        #region Static and Instance Variables

        /// <summary>
        /// VS Project extentions
        /// </summary>
        private static readonly string[] PROJECT_EXTENSIONS = { ".csproj", ".vbproj", ".vjsproj", ".vcproj", ".fsproj" };

        /// <summary>
        /// VS Solution extension
        /// </summary>
        private const string SOLUTION_EXTENSION = ".sln";

        private static readonly Regex netFramework = new Regex("^net[1-9]");

        /// <summary>
        /// The XML representation of the project
        /// </summary>
        private XmlDocument _doc;

        /// <summary>
        /// The list of all our configs
        /// </summary>
        private IDictionary<string, ProjectConfig> _configs = new Dictionary<string, ProjectConfig>();

        #endregion

        #region Constructor

        public VSProject(string projectPath)
        {
            ProjectPath = Path.GetFullPath(projectPath);

            Load();
        }

        #endregion

        #region IProject Members

        /// <summary>
        /// The path to the project
        /// </summary>
        public string ProjectPath { get; private set; }

        /// <summary>
        /// Gets the active configuration, as defined
        /// by the particular project. For a VS
        /// project, we use the first config found.
        /// </summary>
        public string ActiveConfigName
        {
            get
            {
                var names = ConfigNames;
                return names.Count > 0 ? names[0] : null;
            }
        }

        public IList<string> ConfigNames
        {
            get
            {
                var names = new List<string>();
                foreach (var name in _configs.Keys)
                    names.Add(name);
                return names;
            }
        }

        public TestPackage GetTestPackage()
        {
            return GetTestPackage(null);
        }

        public TestPackage GetTestPackage(string configName)
        {
            TestPackage package = new TestPackage(ProjectPath);

            string appbase = null;
            foreach (var name in _configs.Keys)
            {
                if (configName == name)
                {
                    var config = _configs[configName];
                    package.AddSubPackage(new TestPackage(config.AssemblyPath));
                    appbase = config.OutputDirectory;

                    break;
                }
            }

            if (appbase != null)
                package.AddSetting("BasePath", appbase);

            return package;
        }

        #endregion

        #region Other Properties

        /// <summary>
        /// The name of the project.
        /// </summary>
        public string Name => Path.GetFileNameWithoutExtension(ProjectPath);

        public string ProjectDir => Path.GetDirectoryName(ProjectPath);

        #endregion

        #region Public Methods

        public static bool IsProjectFile(string path)
        {
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                return false;

            if (path.ToLower().IndexOf("http:") >= 0)
                return false;

            string extension = Path.GetExtension(path);

            foreach (string validExtension in PROJECT_EXTENSIONS)
                if (extension == validExtension)
                    return true;

            return false;
        }

        public static bool IsSolutionFile(string path)
        {
            return Path.GetExtension(path) == SOLUTION_EXTENSION;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Load a project in various ways, depending on the extension.
        /// </summary>
        private void Load()
        {
            if (!IsProjectFile(ProjectPath))
                ThrowInvalidFileType(ProjectPath);

            StreamReader rdr = new StreamReader(ProjectPath, System.Text.Encoding.UTF8);

            try
            {
                _doc = new XmlDocument();
                _doc.Load(rdr);

                string extension = Path.GetExtension(ProjectPath);

                switch (extension)
                {
                    case ".csproj":
                        // We try legacy project first, then new format for .NET Core projects
                        if (!TryLoadLegacyProject())
                            if (!TryLoadSdkProject())
                                LoadMSBuildProject();
                        break;

                    case ".vbproj":
                    case ".vjsproj":
                    case ".fsproj":
                        // We try legacy projects first, as the initial check is simplest
                        if (!TryLoadLegacyProject())
                            LoadMSBuildProject();
                        break;

                    case ".vcproj":
                        LoadLegacyCppProject();
                        break;

                    default:
                        break;
                }
            }
            catch (FileNotFoundException)
            {
                throw;
            }
            catch (Exception e)
            {
                ThrowInvalidFormat(ProjectPath, e);
            }
            finally
            {
                rdr.Close();
            }
        }

        /// <summary>
        /// Load a project in the SDK project format.
        /// </summary>
        /// <returns>True if the project was successfully loaded, false otherwise.</returns>
        private bool TryLoadSdkProject()
        {
            XmlNode root = _doc.SelectSingleNode("Project");

            if (root != null && SafeAttributeValue(root, "Sdk") != null)
            {
                string[] targetFrameworks =
                    _doc.SelectSingleNode("Project/PropertyGroup/TargetFrameworks")?.InnerText?.Split(new[] { ';' });

                // TODO: Not currently handling multiple targets. That's a separate issue.
                // This code only handles use of TargetFrameworks with a single value.
                string targetFramework = targetFrameworks != null && targetFrameworks.Length > 0
                    ? targetFrameworks[0]
                    : _doc.SelectSingleNode("Project/PropertyGroup/TargetFramework")?.InnerText;

                XmlNode assemblyNameNode = _doc.SelectSingleNode("Project/PropertyGroup/AssemblyName");

                // Even console apps are dll's even if <OutputType> has value 'EXE',
                // if TargetFramework is netcore
                string outputType = "dll";

                if (netFramework.IsMatch(targetFramework))
                {
                    // When targetting standard .Net framework, the default is still dll,
                    // however, OutputType is now honoured.
                    // Also if Sdk = 'Microsoft.NET.Sdk.Web' then OutputType default is exe
                    string sdk = root.Attributes["Sdk"].Value;

                    if (sdk == "Microsoft.NET.Sdk.Web")
                    {
                        outputType = "exe";
                    }
                    else
                    {
                        XmlNode outputTypeNode = _doc.SelectSingleNode("Project/PropertyGroup/OutputType");
                        if (outputTypeNode != null && outputTypeNode.InnerText != "Library")
                        {
                            outputType = "exe";
                        }
                    }
                }

                string assemblyName = assemblyNameNode == null ? $"{Name}.{outputType}" : $"{assemblyNameNode.InnerText}.{outputType}";

                var appendTargetFrameworkNode = _doc.SelectSingleNode("Project/PropertyGroup/AppendTargetFrameworkToOutputPath");
                bool appendTargetFramework = appendTargetFrameworkNode == null || appendTargetFrameworkNode.InnerText.ToLower() == "true";

                XmlNodeList nodes = _doc.SelectNodes("/Project/PropertyGroup");

                string commonOutputPath = null;

                foreach (XmlElement configNode in nodes)
                {
                    string configName = GetConfigNameFromCondition(configNode);

                    XmlElement outputPathElement = (XmlElement)configNode.SelectSingleNode("OutputPath");
                    string outputPath = outputPathElement?.InnerText;

                    if (outputPath == null)
                        continue;

                    if (configName == null)
                    {
                        commonOutputPath = outputPath;
                        continue;
                    }

                    if (appendTargetFramework)
                    {
                        var suffix = "/" + targetFramework;
                        if (!outputPath.EndsWith(suffix))
                            outputPath += suffix;
                    }

                    _configs.Add(configName, CreateProjectConfig(configName, outputPath, assemblyName));
                }

                // By convention there is a Debug and a Release configuration unless others are explicitly 
                // mentioned in the project file. If we have less than 2 then at least one of those is missing.
                // We cannot tell however if the existing configuration is meant to replace Debug or Release.
                // Therefore we just add what is missing. The one that has been replaced will not be used.
                if (_configs.Count < 2)
                {
                    if (!_configs.ContainsKey("Debug"))
                    {
                        string configName = "Debug";
                        string outputPath = commonOutputPath != null
                            ? commonOutputPath.Replace("$(Configuration)", configName)
                            : $@"bin\{configName}";
                        if (appendTargetFramework)
                            outputPath += "/" + targetFramework;
                        _configs.Add(configName, CreateProjectConfig(configName, outputPath, assemblyName));
                    }
                    if (!_configs.ContainsKey("Release"))
                    {
                        string configName = "Release";
                        string outputPath = commonOutputPath != null
                            ? commonOutputPath.Replace("$(Configuration)", configName)
                            : Path.Combine("bin", configName);
                        if (appendTargetFramework)
                            outputPath = Path.Combine(outputPath, targetFramework);
                        _configs.Add(configName, CreateProjectConfig(configName, outputPath, assemblyName));
                    }
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Load a project in the legacy VS2003 format. Note that this method is not 
        /// called for C++ projects using the same format, because the details differ.
        /// </summary>
        /// <returns>True if this project is in the VS2003 format, otherwise false.</returns>
        private bool TryLoadLegacyProject()
        {
            XmlNode settingsNode = _doc.SelectSingleNode("/VisualStudioProject/*/Build/Settings");
            if (settingsNode == null)
                return false;

            string assemblyName = RequiredAttributeValue(settingsNode, "AssemblyName");
            string outputType = RequiredAttributeValue(settingsNode, "OutputType");

            if (outputType == "Exe" || outputType == "WinExe")
                assemblyName = assemblyName + ".exe";
            else
                assemblyName = assemblyName + ".dll";

            XmlNodeList nodes = settingsNode.SelectNodes("Config");
            if (nodes != null)
                foreach (XmlNode configNode in nodes)
                {
                    string name = RequiredAttributeValue(configNode, "Name");
                    string outputPath = RequiredAttributeValue(configNode, "OutputPath");

                    _configs.Add(name, CreateProjectConfig(name, outputPath, assemblyName));
                }

            return true;
        }

        /// <summary>
        /// Load a non-C++ project in the MsBuild format introduced with VS2005
        /// </summary>
        private void LoadMSBuildProject()
        {
            XmlNamespaceManager namespaceManager = new XmlNamespaceManager(_doc.NameTable);
            namespaceManager.AddNamespace("msbuild", "http://schemas.microsoft.com/developer/msbuild/2003");

            XmlNodeList nodes = _doc.SelectNodes("/msbuild:Project/msbuild:PropertyGroup", namespaceManager);
            if (nodes == null) return;

            XmlElement assemblyNameElement = (XmlElement)_doc.SelectSingleNode("/msbuild:Project/msbuild:PropertyGroup/msbuild:AssemblyName", namespaceManager);
            string assemblyName = assemblyNameElement == null ? Name : assemblyNameElement.InnerText;

            XmlElement outputTypeElement = (XmlElement)_doc.SelectSingleNode("/msbuild:Project/msbuild:PropertyGroup/msbuild:OutputType", namespaceManager);
            string outputType = outputTypeElement == null ? "Library" : outputTypeElement.InnerText;

            if (outputType == "Exe" || outputType == "WinExe")
                assemblyName = assemblyName + ".exe";
            else
                assemblyName = assemblyName + ".dll";

            string commonOutputPath = null;
            var explicitOutputPaths = new Dictionary<string, string>();

            foreach (XmlElement configNode in nodes)
            {
                string name = GetConfigNameFromCondition(configNode);

                XmlElement outputPathElement = (XmlElement)configNode.SelectSingleNode("msbuild:OutputPath", namespaceManager);
                string outputPath = null;
                if (outputPathElement != null)
                    outputPath = outputPathElement.InnerText;

                if (name == null)
                {
                    if (outputPathElement != null)
                        commonOutputPath = outputPath;
                    continue;
                }

                if (outputPathElement != null)
                    explicitOutputPaths[name] = outputPath;

                if (outputPath == null)
                    outputPath = explicitOutputPaths.ContainsKey(name) ? explicitOutputPaths[name] : commonOutputPath;

                if (outputPath != null)
                    _configs[name] = CreateProjectConfig(name, outputPath.Replace("$(Configuration)", name), assemblyName);
            }
        }

        /// <summary>
        /// Load a C++ project in the legacy format, which was used for C++
        /// much longer than it was used for the other languages supported.
        /// </summary>
        private void LoadLegacyCppProject()
        {
            string[] extensionsByConfigType = { "", ".exe", ".dll", ".lib", "" };

            // TODO: This is all very hacked up... replace it.
            foreach (XmlNode configNode in _doc.SelectNodes("/VisualStudioProject/Configurations/Configuration"))
            {
                string name = RequiredAttributeValue(configNode, "Name");
                int config_type = System.Convert.ToInt32(RequiredAttributeValue(configNode, "ConfigurationType"));
                string dirName = name;
                int bar = dirName.IndexOf('|');
                if (bar >= 0)
                    dirName = dirName.Substring(0, bar);
                string outputPath = RequiredAttributeValue(configNode, "OutputDirectory");
                outputPath = outputPath.Replace("$(SolutionDir)", Path.GetFullPath(Path.GetDirectoryName(ProjectPath)) + Path.DirectorySeparatorChar);
                outputPath = outputPath.Replace("$(ConfigurationName)", dirName);

                XmlNode toolNode = configNode.SelectSingleNode("Tool[@Name='VCLinkerTool']");
                string assemblyName = null;
                if (toolNode != null)
                {
                    assemblyName = SafeAttributeValue(toolNode, "OutputFile");
                    if (assemblyName != null)
                        assemblyName = Path.GetFileName(assemblyName);
                    else
                        assemblyName = Path.GetFileNameWithoutExtension(ProjectPath) + extensionsByConfigType[config_type];
                }
                else
                {
                    toolNode = configNode.SelectSingleNode("Tool[@Name='VCNMakeTool']");
                    if (toolNode != null)
                        assemblyName = Path.GetFileName(RequiredAttributeValue(toolNode, "Output"));
                }

                assemblyName = assemblyName.Replace("$(OutDir)", outputPath);
                assemblyName = assemblyName.Replace("$(ProjectName)", Name);

                _configs.Add(name, CreateProjectConfig(name, outputPath, assemblyName));
            }
        }

        private void ThrowInvalidFileType(string projectPath)
        {
            throw new ArgumentException(
                string.Format("Invalid project file type: {0}",
                                Path.GetFileName(projectPath)));
        }

        private void ThrowInvalidFormat(string projectPath, Exception e)
        {
            throw new ArgumentException(
                string.Format("Invalid project file format: {0}",
                                Path.GetFileName(projectPath)), e);
        }

        private string SafeAttributeValue(XmlNode node, string attrName)
        {
            XmlNode attrNode = node.Attributes[attrName];
            return attrNode == null ? null : attrNode.Value;
        }

        private string RequiredAttributeValue(XmlNode node, string name)
        {
            string result = SafeAttributeValue(node, name);
            if (result != null)
                return result;

            throw new ApplicationException("Missing required attribute " + name);
        }

        private static string GetConfigNameFromCondition(XmlElement configNode)
        {
            string configurationName = null;
            XmlAttribute conditionAttribute = configNode.Attributes["Condition"];
            if (conditionAttribute != null)
            {
                string condition = conditionAttribute.Value;
                if (condition.IndexOf("$(Configuration)") >= 0)
                {
                    int start = condition.IndexOf("==");
                    if (start >= 0)
                    {
                        configurationName = condition.Substring(start + 2).Trim(new char[] { ' ', '\'' });
                        int bar = configurationName.IndexOf('|');
                        if (bar > 0)
                            configurationName = configurationName.Substring(0, bar);
                    }
                }
            }
            return configurationName;
        }

        private ProjectConfig CreateProjectConfig(string name, string outputPath, string assemblyName)
        {
            var assemblyPath = Path.Combine(Path.Combine(ProjectDir, outputPath), assemblyName);
            return new ProjectConfig(name, assemblyPath);
        }

        #endregion

        #region Nested ProjectConfig Class

        private class ProjectConfig
        {
            public ProjectConfig(IProject project, string name, string outputDirectory, string assemblyName)
            {
                Name = name;
                OutputDirectory = Normalize(Path.Combine(Path.GetDirectoryName(project.ProjectPath), outputDirectory));
                AssemblyPath = Normalize(Path.Combine(OutputDirectory, assemblyName));
            }

            public ProjectConfig(string name, string assemblyPath)
            {
                Name = name;
                AssemblyPath = Normalize(assemblyPath);
                OutputDirectory = Path.GetDirectoryName(AssemblyPath);
            }

            public string Name { get; }

            public string OutputDirectory { get; }

            public string AssemblyPath { get; }

            private static string Normalize(string path)
            {
                char sep = Path.DirectorySeparatorChar;

                if (sep != '\\')
                    path = path.Replace('\\', sep);

                return path;
            }
        }

        #endregion
    }
}
