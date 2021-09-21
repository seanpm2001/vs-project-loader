// ***********************************************************************
// Copyright (c) 2008-2014 Charlie Poole
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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Engine.Extensibility;
using NUnit.Engine.Tests.resources;
using NUnit.Framework;
using System.Text.RegularExpressions;

namespace NUnit.Engine.Services.ProjectLoaders.Tests
{
    public abstract class ProjectLoaderTests
    {
        protected static readonly Regex PathSeparatorLookup = new Regex(@"[/\\]");
        protected VisualStudioProjectLoader _loader;

        [SetUp]
        public void CreateLoader()
        {
            _loader = new VisualStudioProjectLoader();
        }

        protected void CanLoadProject(ProjectData projectData)
        {
            Assert.That(_loader.CanLoadFrom(projectData.ProjectName));

            using (TestResource file = new TestResource(projectData.ProjectName))
            {
                IProject project = _loader.LoadFrom(file.Path);

                Assert.That(project.ConfigNames, Is.EquivalentTo(projectData.ConfigNames));

                foreach (var config in projectData.ConfigNames)
                {
                    TestPackage package = project.GetTestPackage(config);
                    ConfigData configData = projectData.Configs[config];

                    Assert.AreEqual(projectData.ProjectName, package.Name);
                    Assert.AreEqual(1, package.SubPackages.Count);
                    Assert.AreEqual(projectData.AssemblyName, Path.GetFileNameWithoutExtension(package.SubPackages[0].FullName));
                    Assert.That(package.Settings.ContainsKey("BasePath"));
                    Assert.That(Path.GetDirectoryName(package.SubPackages[0].FullName), Is.SamePath((string)package.Settings["BasePath"]));
                    string projectDir = Path.GetDirectoryName(file.Path);
                    Assert.That(Path.GetDirectoryName(package.SubPackages[0].FullName), Is.SamePath(Path.Combine(projectDir, configData.OutputPath)));
                }
            }
        }

        /// <summary>
        /// Nested ProjectData class
        /// </summary>
        public class ProjectData
        {
            private string[] DEFAULT_CONFIGS = new[] { "Debug", "Release" };
            private Dictionary<string, ConfigData> _configs;

            public ProjectData(string projectName, bool appendTargetRuntimes = false)
            {
                ProjectName = projectName;

                // Set default value. Set property to override.
                AssemblyName = Path.GetFileNameWithoutExtension(projectName);
            }

            public string ProjectName { get; }

            public IDictionary<string, ConfigData> Configs
            {
                get
                {
                    if (_configs == null)
                    {
                        // Set up default configs
                        _configs = new Dictionary<string, ConfigData>();

                        foreach (var config in DEFAULT_CONFIGS)
                        {
                            var outputPath = _runtimeDirectory != null
                                ? $"bin/{config}/{_runtimeDirectory}/"
                                : $"bin/{config}/";
                            _configs.Add(config, new ConfigData(config, outputPath));
                        }
                    }

                    return _configs;
                }
            }

            public string[] ConfigNames => Configs.Keys.ToArray();
            public string AssemblyName { get; private set; }

            //////////////////////////////////////////////////////
            // Fluent property setters
            //////////////////////////////////////////////////////

            /// <summary>
            /// Specify a non-default assembly name
            /// </summary>
            /// <param name="name">The assemlby name</param>
            /// <returns>Self</returns>
            public ProjectData Named(string name)
            {
                AssemblyName = name;
                return this;
            }

            string _runtimeDirectory;
            public ProjectData RuntimeDirectory(string runtimeDirectory)
            {
                _runtimeDirectory = runtimeDirectory;
                return this;
            }

            /// <summary>
            /// Add a list of configs to the project data, using default output path.
            /// </summary>
            /// <param name="names">Config names</param>
            /// <returns>Self</returns>
            public ProjectData WithConfigs(params string[] names)
            {
                var result = this;

                foreach (string name in names)
                    result = result.WithConfig(name);

                return result; 
            }

            /// <summary>
            /// Add a config to the project data
            /// </summary>
            /// <param name="name">The name of the config</param>
            /// <param name="outputPath">Optional path to the directory used for output.</param>
            /// <returns>Self</returns>
            public ProjectData WithConfig(string name, string outputPath = null)
            {
                if (_configs == null)
                    _configs = new Dictionary<string, ConfigData>();

                if (outputPath == null)
                {
                    outputPath = $"bin/{name}/";
                    if (_runtimeDirectory != null)
                        outputPath += _runtimeDirectory + "/";
                }

                _configs.Add(name, new ConfigData(name, outputPath));

                return this;
            }

            /// <summary>
            /// Override used when displaying test error messages.
            /// </summary>
            /// <returns></returns>
            public override string ToString()
            {
                return ProjectName;
            }
        }

        /// <summary>
        /// Nested ConfigData class
        /// </summary>
        public class ConfigData
        {
            public ConfigData(string name, string outputPath)
            {
                Name = name;
                OutputPath = outputPath;
            }
            public string Name { get; }
            public string OutputPath { get; }
        }

        protected static string NormalizePath(string path)
        {
            return PathSeparatorLookup.Replace(path, Path.DirectorySeparatorChar.ToString());
        }
    }
}
