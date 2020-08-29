using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Palmmedia.ReportGenerator.Core.Common;
using Palmmedia.ReportGenerator.Core.Logging;
using Palmmedia.ReportGenerator.Core.Parser.Analysis;
using Palmmedia.ReportGenerator.Core.Parser.Filtering;
using Palmmedia.ReportGenerator.Core.Properties;

namespace Palmmedia.ReportGenerator.Core.Parser
{
    /// <summary>
    /// Parser for XML reports generated by OpenCover.
    /// </summary>
    internal class OpenCoverParser : ParserBase
    {
        /// <summary>
        /// The Logger.
        /// </summary>
        private static readonly ILogger Logger = LoggerFactory.GetLogger(typeof(OpenCoverParser));

        /// <summary>
        /// Regex to analyze if a method name belongs to a lamda expression.
        /// </summary>
        private static Regex lambdaMethodNameRegex = new Regex("::<.+>.+__", RegexOptions.Compiled);

        /// <summary>
        /// Regex to analyze if a method name is generated by compiler.
        /// </summary>
        private static Regex compilerGeneratedMethodNameRegex = new Regex(@"<(?<CompilerGeneratedName>.+)>.+__.+::MoveNext\(\)$", RegexOptions.Compiled);

        /// <summary>
        /// Regex to extract short method name.
        /// </summary>
        private static Regex methodRegex = new Regex(@"^.*::(?<MethodName>.+)\((?<Arguments>.*)\)$", RegexOptions.Compiled);

        /// <summary>
        /// Cache for method names.
        /// </summary>
        private static ConcurrentDictionary<string, string> methodNameMap = new ConcurrentDictionary<string, string>();

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenCoverParser" /> class.
        /// </summary>
        /// <param name="assemblyFilter">The assembly filter.</param>
        /// <param name="classFilter">The class filter.</param>
        /// <param name="fileFilter">The file filter.</param>
        internal OpenCoverParser(IFilter assemblyFilter, IFilter classFilter, IFilter fileFilter)
            : base(assemblyFilter, classFilter, fileFilter)
        {
        }

        /// <summary>
        /// Parses the given XML report.
        /// </summary>
        /// <param name="report">The XML report.</param>
        /// <returns>The parser result.</returns>
        public ParserResult Parse(XContainer report)
        {
            if (report == null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            var assemblies = new List<Assembly>();

            var modules = report.Descendants("Module")
                .Where(m => m.Attribute("skippedDueTo") == null)
                .ToArray();
            var files = report.Descendants("File").ToArray();

            var trackedMethods = new Dictionary<string, string>();

            foreach (var trackedMethodElement in report.Descendants("TrackedMethod"))
            {
                if (trackedMethods.ContainsKey(trackedMethodElement.Attribute("uid").Value))
                {
                    Logger.WarnFormat(
                        Resources.ErrorNotUniqueTrackedMethodUid,
                        trackedMethodElement.Attribute("name").Value);

                    trackedMethods.Clear();

                    break;
                }
                else
                {
                    trackedMethods.Add(trackedMethodElement.Attribute("uid").Value, trackedMethodElement.Attribute("name").Value);
                }
            }

            var assemblyNames = modules
                .Select(m => m.Element("ModuleName").Value)
                .Distinct()
                .Where(a => this.AssemblyFilter.IsElementIncludedInReport(a))
                .OrderBy(a => a)
                .ToArray();

            var assemblyModules = assemblyNames.
                ToDictionary(
                    k => k,
                    v => modules.Where(t => t.Element("ModuleName").Value.Equals(v)).ToArray());

            foreach (var assemblyName in assemblyNames)
            {
                assemblies.Add(this.ProcessAssembly(assemblyModules, files, trackedMethods, assemblyName));
            }

            var result = new ParserResult(assemblies.OrderBy(a => a.Name).ToList(), true, this.ToString());
            return result;
        }

        /// <summary>
        /// Processes the given assembly.
        /// </summary>
        /// <param name="assemblyModules">The modules belonging to a assembly name.</param>
        /// <param name="files">The files.</param>
        /// <param name="trackedMethods">The tracked methods.</param>
        /// <param name="assemblyName">Name of the assembly.</param>
        /// <returns>The <see cref="Assembly"/>.</returns>
        private Assembly ProcessAssembly(IDictionary<string, XElement[]> assemblyModules, XElement[] files, IDictionary<string, string> trackedMethods, string assemblyName)
        {
            Logger.DebugFormat(Resources.CurrentAssembly, assemblyName);

            var fileIdsByFilename = assemblyModules[assemblyName]
                .Elements("Files")
                .Elements("File")
                .GroupBy(f => f.Attribute("fullPath").Value, f => f.Attribute("uid").Value)
                .ToDictionary(g => g.Key, g => g.ToHashSet());

            var classNames = assemblyModules[assemblyName]
                .Elements("Classes")
                .Elements("Class")
                .Where(c => c.Attribute("skippedDueTo") == null)
                .Select(c =>
                    {
                        string fullname = c.Element("FullName").Value;
                        int nestedClassSeparatorIndex = fullname.IndexOf('/');
                        return nestedClassSeparatorIndex > -1 ? fullname.Substring(0, nestedClassSeparatorIndex) : fullname;
                    })
                .Where(name => !name.Contains("<"))
                .Distinct()
                .Where(c => this.ClassFilter.IsElementIncludedInReport(c))
                .OrderBy(name => name)
                .ToArray();

            var assembly = new Assembly(assemblyName);

            Parallel.ForEach(classNames, className => this.ProcessClass(assemblyModules, files, trackedMethods, fileIdsByFilename, assembly, className));

            return assembly;
        }

        /// <summary>
        /// Processes the given class.
        /// </summary>
        /// <param name="assemblyModules">The modules belonging to a assembly name.</param>
        /// <param name="files">The files.</param>
        /// <param name="trackedMethods">The tracked methods.</param>
        /// <param name="fileIdsByFilename">Dictionary containing the file ids by filename.</param>
        /// <param name="assembly">The assembly.</param>
        /// <param name="className">Name of the class.</param>
        private void ProcessClass(IDictionary<string, XElement[]> assemblyModules, XElement[] files, IDictionary<string, string> trackedMethods, Dictionary<string, HashSet<string>> fileIdsByFilename, Assembly assembly, string className)
        {
            var methods = assemblyModules[assembly.Name]
                .Elements("Classes")
                .Elements("Class")
                .Where(c => c.Element("FullName").Value.Equals(className)
                            || c.Element("FullName").Value.StartsWith(className + "/", StringComparison.Ordinal))
                .Elements("Methods")
                .Elements("Method")
                .Where(m => m.Attribute("skippedDueTo") == null)
                .ToArray();

            var fileIdsOfClassInSequencePoints = methods
                .Elements("SequencePoints")
                .Elements("SequencePoint")
                .Select(seqpnt => seqpnt.Attribute("fileid")?.Value)
                .Where(seqpnt => seqpnt != null && seqpnt != "0")
                .ToArray();

            // Only required for backwards compatibility, older versions of OpenCover did not apply fileid for partial classes
            var fileIdsOfClassInFileRef = methods
                .Select(m => m.Element("FileRef")?.Attribute("uid").Value)
                .Where(m => m != null)
                .ToArray();

            var fileIdsOfClass = fileIdsOfClassInSequencePoints
                .Concat(fileIdsOfClassInFileRef)
                .Distinct()
                .ToHashSet();

            var filesOfClass = files
                .Where(file => fileIdsOfClass.Contains(file.Attribute("uid").Value))
                .Select(file => file.Attribute("fullPath").Value)
                .Distinct()
                .ToArray();

            var filteredFilesOfClass = filesOfClass
                .Where(f => this.FileFilter.IsElementIncludedInReport(f))
                .ToArray();

            // If all files are removed by filters, then the whole class is omitted
            if ((filesOfClass.Length == 0 && !this.FileFilter.HasCustomFilters) || filteredFilesOfClass.Length > 0)
            {
                var @class = new Class(className, assembly);

                foreach (var file in filteredFilesOfClass)
                {
                    @class.AddFile(ProcessFile(trackedMethods, fileIdsByFilename[file], file, methods));
                }

                @class.CoverageQuota = GetCoverageQuotaOfClass(methods);

                assembly.AddClass(@class);
            }
        }

        /// <summary>
        /// Processes the file.
        /// </summary>
        /// <param name="trackedMethods">The tracked methods.</param>
        /// <param name="fileIds">The file ids of the class.</param>
        /// <param name="filePath">The file path.</param>
        /// <param name="methods">The methods.</param>
        /// <returns>The <see cref="CodeFile"/>.</returns>
        private static CodeFile ProcessFile(IDictionary<string, string> trackedMethods, HashSet<string> fileIds, string filePath, XElement[] methods)
        {
            var seqpntsOfFile = methods
                .Elements("SequencePoints")
                .Elements("SequencePoint")
                .Where(seqpnt => (seqpnt.Attribute("fileid") != null
                                    && fileIds.Contains(seqpnt.Attribute("fileid").Value))
                    || (seqpnt.Attribute("fileid") == null && seqpnt.Parent.Parent.Element("FileRef") != null
                        && fileIds.Contains(seqpnt.Parent.Parent.Element("FileRef").Attribute("uid").Value)))
                .Select(seqpnt => new
                {
                    LineNumberStart = int.Parse(seqpnt.Attribute("sl").Value, CultureInfo.InvariantCulture),
                    LineNumberEnd = seqpnt.Attribute("el") != null ? int.Parse(seqpnt.Attribute("el").Value, CultureInfo.InvariantCulture) : int.Parse(seqpnt.Attribute("sl").Value, CultureInfo.InvariantCulture),
                    Visits = seqpnt.Attribute("vc").Value.ParseLargeInteger(),
                    TrackedMethodRefs = seqpnt.Elements("TrackedMethodRefs")
                        .Elements("TrackedMethodRef")
                        .Select(t => new
                        {
                            Visits = t.Attribute("vc").Value.ParseLargeInteger(),
                            TrackedMethodId = t.Attribute("uid").Value
                        })
                })
                .OrderBy(seqpnt => seqpnt.LineNumberEnd)
                .ToArray();

            var branches = GetBranches(methods, fileIds);

            var coverageByTrackedMethod = seqpntsOfFile
                .SelectMany(s => s.TrackedMethodRefs)
                .Select(t => t.TrackedMethodId)
                .Distinct()
                .ToDictionary(id => id, id => new CoverageByTrackedMethod { Coverage = new int[] { }, LineVisitStatus = new LineVisitStatus[] { } });

            int[] coverage = new int[] { };
            LineVisitStatus[] lineVisitStatus = new LineVisitStatus[] { };

            if (seqpntsOfFile.Length > 0)
            {
                coverage = new int[seqpntsOfFile[seqpntsOfFile.LongLength - 1].LineNumberEnd + 1];
                lineVisitStatus = new LineVisitStatus[seqpntsOfFile[seqpntsOfFile.LongLength - 1].LineNumberEnd + 1];

                for (int i = 0; i < coverage.Length; i++)
                {
                    coverage[i] = -1;
                }

                foreach (var trackedMethodCoverage in coverageByTrackedMethod)
                {
                    trackedMethodCoverage.Value.Coverage = (int[])coverage.Clone();
                    trackedMethodCoverage.Value.LineVisitStatus = new LineVisitStatus[seqpntsOfFile[seqpntsOfFile.LongLength - 1].LineNumberEnd + 1];
                }

                foreach (var seqpnt in seqpntsOfFile)
                {
                    for (int lineNumber = seqpnt.LineNumberStart; lineNumber <= seqpnt.LineNumberEnd; lineNumber++)
                    {
                        int visits = coverage[lineNumber] == -1 ? seqpnt.Visits : coverage[lineNumber] + seqpnt.Visits;
                        coverage[lineNumber] = visits;

                        if (lineVisitStatus[lineNumber] != LineVisitStatus.Covered)
                        {
                            bool partiallyCovered = false;

                            ICollection<Branch> branchesOfLine = null;

                            // Use 'LineNumberStart' instead of 'lineNumber' here. Branches have line number of first line of seqpnt
                            if (branches.TryGetValue(seqpnt.LineNumberStart, out branchesOfLine))
                            {
                                partiallyCovered = branchesOfLine.Any(b => b.BranchVisits == 0);
                            }

                            LineVisitStatus statusOfLine = visits > 0 ? (partiallyCovered ? LineVisitStatus.PartiallyCovered : LineVisitStatus.Covered) : LineVisitStatus.NotCovered;
                            lineVisitStatus[lineNumber] = (LineVisitStatus)Math.Max((int)lineVisitStatus[lineNumber], (int)statusOfLine);
                        }

                        if (visits > -1)
                        {
                            foreach (var trackedMethodCoverage in coverageByTrackedMethod)
                            {
                                if (trackedMethodCoverage.Value.Coverage[lineNumber] == -1)
                                {
                                    trackedMethodCoverage.Value.Coverage[lineNumber] = 0;
                                    trackedMethodCoverage.Value.LineVisitStatus[lineNumber] = LineVisitStatus.NotCovered;
                                }
                            }
                        }

                        foreach (var trackedMethod in seqpnt.TrackedMethodRefs)
                        {
                            var trackedMethodCoverage = coverageByTrackedMethod[trackedMethod.TrackedMethodId];

                            int trackeMethodVisits = trackedMethodCoverage.Coverage[lineNumber] == -1 ? trackedMethod.Visits : trackedMethodCoverage.Coverage[lineNumber] + trackedMethod.Visits;
                            LineVisitStatus statusOfLine = trackeMethodVisits > 0 ? (LineVisitStatus)Math.Min((int)LineVisitStatus.Covered, (int)lineVisitStatus[lineNumber]) : LineVisitStatus.NotCovered;

                            trackedMethodCoverage.Coverage[lineNumber] = trackeMethodVisits;
                            trackedMethodCoverage.LineVisitStatus[lineNumber] = statusOfLine;
                        }
                    }
                }
            }

            var codeFile = new CodeFile(filePath, coverage, lineVisitStatus, branches);

            foreach (var trackedMethodCoverage in coverageByTrackedMethod)
            {
                string name = null;

                // Sometimes no corresponding MethodRef element exists
                if (trackedMethods.TryGetValue(trackedMethodCoverage.Key, out name))
                {
                    string shortName = name.Substring(name.Substring(0, name.IndexOf(':') + 1).LastIndexOf('.') + 1);
                    TestMethod testMethod = new TestMethod(name, shortName);
                    codeFile.AddCoverageByTestMethod(testMethod, trackedMethodCoverage.Value);
                }
            }

            var methodsOfFile = methods
                .Where(m => m.Element("FileRef") != null && fileIds.Contains(m.Element("FileRef").Attribute("uid").Value))
                .ToArray();

            SetMethodMetrics(codeFile, methodsOfFile);
            SetCodeElements(codeFile, methodsOfFile);

            return codeFile;
        }

        /// <summary>
        /// Extracts the metrics from the given <see cref="XElement">XElements</see>.
        /// </summary>
        /// <param name="codeFile">The code file.</param>
        /// <param name="methodsOfFile">The methods of the file.</param>
        private static void SetMethodMetrics(CodeFile codeFile, IEnumerable<XElement> methodsOfFile)
        {
            foreach (var methodGroup in methodsOfFile.GroupBy(m => m.Element("Name").Value))
            {
                var method = methodGroup.First();

                // Exclude properties and lambda expressions
                if (method.Attribute("skippedDueTo") != null
                    || method.HasAttributeWithValue("isGetter", "true")
                    || method.HasAttributeWithValue("isSetter", "true")
                    || lambdaMethodNameRegex.IsMatch(methodGroup.Key))
                {
                    continue;
                }

                var metrics = new List<Metric>()
                {
                    new Metric(
                        ReportResources.CyclomaticComplexity,
                        ParserBase.CyclomaticComplexityUri,
                        MetricType.CodeQuality,
                        methodGroup.Max(m => int.Parse(m.Attribute("cyclomaticComplexity").Value, CultureInfo.InvariantCulture)),
                        MetricMergeOrder.LowerIsBetter),
                    new Metric(
                        ReportResources.SequenceCoverage,
                        ParserBase.CodeCoverageUri,
                        MetricType.CoveragePercentual,
                        methodGroup.Max(m => decimal.Parse(m.Attribute("sequenceCoverage").Value, CultureInfo.InvariantCulture))),
                    new Metric(
                        ReportResources.BranchCoverage,
                        ParserBase.CodeCoverageUri,
                        MetricType.CoveragePercentual,
                        methodGroup.Max(m => decimal.Parse(m.Attribute("branchCoverage").Value, CultureInfo.InvariantCulture)))
                };

                var npathComplexityAttributes = methodGroup.Select(m => m.Attribute("nPathComplexity")).Where(a => a != null).ToArray();

                if (npathComplexityAttributes.Length > 0)
                {
                    metrics.Insert(
                        1,
                        new Metric(
                        ReportResources.NPathComplexity,
                        ParserBase.NPathComplexityUri,
                        MetricType.CodeQuality,
                        npathComplexityAttributes
                            .Select(a => int.Parse(a.Value, CultureInfo.InvariantCulture))
                            .Max(a => a < 0 ? int.MaxValue : a),
                        MetricMergeOrder.LowerIsBetter));
                }

                var crapScoreAttributes = methodGroup.Select(m => m.Attribute("crapScore")).Where(a => a != null).ToArray();
                if (crapScoreAttributes.Length > 0)
                {
                    metrics.Add(new Metric(
                        ReportResources.CrapScore,
                        ParserBase.CrapScoreUri,
                        MetricType.CodeQuality,
                        crapScoreAttributes.Max(a => decimal.Parse(a.Value, CultureInfo.InvariantCulture)),
                        MetricMergeOrder.LowerIsBetter));
                }

                string fullName = ExtractMethodName(methodGroup.Key);
                string shortName = methodRegex.Replace(fullName, m => string.Format(CultureInfo.InvariantCulture, "{0}({1})", m.Groups["MethodName"].Value, m.Groups["Arguments"].Value.Length > 0 ? "..." : string.Empty));

                var methodMetric = new MethodMetric(fullName, shortName, metrics);

                var seqpnt = method
                    .Elements("SequencePoints")
                    .Elements("SequencePoint")
                    .FirstOrDefault();

                if (seqpnt != null)
                {
                    methodMetric.Line = int.Parse(seqpnt.Attribute("sl").Value, CultureInfo.InvariantCulture);
                }

                codeFile.AddMethodMetric(methodMetric);
            }
        }

        /// <summary>
        /// Gets the branches by line number.
        /// </summary>
        /// <param name="methods">The methods.</param>
        /// <param name="fileIds">The file ids of the class.</param>
        /// <returns>The branches by line number.</returns>
        private static Dictionary<int, ICollection<Branch>> GetBranches(XElement[] methods, HashSet<string> fileIds)
        {
            var branchPoints = methods
                .Elements("BranchPoints")
                .Elements("BranchPoint")
                .ToArray();

            // OpenCover supports this since version 4.5.3207
            if (branchPoints.Length == 0 || branchPoints[0].Attribute("sl") == null)
            {
                return new Dictionary<int, ICollection<Branch>>();
            }

            var result = new Dictionary<int, Dictionary<string, Branch>>();
            foreach (var branchPoint in branchPoints)
            {
                if (branchPoint.Attribute("fileid") != null
                    && !fileIds.Contains(branchPoint.Attribute("fileid").Value))
                {
                    // If fileid is available, verify that branch belongs to same file (available since version OpenCover.4.5.3418)
                    continue;
                }

                int lineNumber = int.Parse(branchPoint.Attribute("sl").Value, CultureInfo.InvariantCulture);

                string identifier = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}_{1}_{2}_{3}",
                    lineNumber,
                    branchPoint.Attribute("path").Value,
                    branchPoint.Attribute("offset").Value,
                    branchPoint.Attribute("offsetend").Value);
                int vc = int.Parse(branchPoint.Attribute("vc").Value, CultureInfo.InvariantCulture);

                if (result.TryGetValue(lineNumber, out var branches))
                {
                    if (branches.TryGetValue(identifier, out var found))
                    {
                        found.BranchVisits += vc;
                    }
                    else
                    {
                        branches.Add(identifier, new Branch(vc, identifier));
                    }
                }
                else
                {
                    branches = new Dictionary<string, Branch>();
                    branches.Add(identifier, new Branch(vc, identifier));
                    result.Add(lineNumber, branches);
                }
            }

            return result.ToDictionary(k => k.Key, v => (ICollection<Branch>)v.Value.Values.ToHashSet());
        }

        /// <summary>
        /// Extracts the methods/properties of the given <see cref="XElement">XElements</see>.
        /// </summary>
        /// <param name="codeFile">The code file.</param>
        /// <param name="methodsOfFile">The methods of the file.</param>
        private static void SetCodeElements(CodeFile codeFile, IEnumerable<XElement> methodsOfFile)
        {
            foreach (var method in methodsOfFile)
            {
                if (method.Attribute("skippedDueTo") != null
                    || lambdaMethodNameRegex.IsMatch(method.Element("Name").Value))
                {
                    continue;
                }

                string methodName = ExtractMethodName(method.Element("Name").Value);
                methodName = methodName.Substring(methodName.LastIndexOf(':') + 1);

                CodeElementType type = CodeElementType.Method;

                if (method.HasAttributeWithValue("isGetter", "true")
                    || method.HasAttributeWithValue("isSetter", "true"))
                {
                    type = CodeElementType.Property;
                    methodName = methodName.Substring(4);
                }

                var seqpnts = method
                    .Elements("SequencePoints")
                    .Elements("SequencePoint")
                    .Select(seqpnt => new
                    {
                        LineNumberStart = int.Parse(seqpnt.Attribute("sl").Value, CultureInfo.InvariantCulture),
                        LineNumberEnd = seqpnt.Attribute("el") != null ? int.Parse(seqpnt.Attribute("el").Value, CultureInfo.InvariantCulture) : int.Parse(seqpnt.Attribute("sl").Value, CultureInfo.InvariantCulture)
                    })
                    .ToArray();

                if (seqpnts.Length > 0)
                {
                    int firstLine = seqpnts.Min(s => s.LineNumberStart);
                    int lastLine = seqpnts.Max(s => s.LineNumberEnd);

                    codeFile.AddCodeElement(new CodeElement(
                        methodName,
                        type,
                        firstLine,
                        lastLine,
                        codeFile.CoverageQuota(firstLine, lastLine)));
                }
            }
        }

        /// <summary>
        /// Extracts the method name. For async methods the original name is returned.
        /// </summary>
        /// <param name="methodName">The full method name.</param>
        /// <returns>The method name.</returns>
        private static string ExtractMethodName(string methodName)
        {
            if (!methodNameMap.TryGetValue(methodName, out var fullName))
            {
                // Quick check before expensive regex is called
                if (methodName.EndsWith("::MoveNext()"))
                {
                    Match match = compilerGeneratedMethodNameRegex.Match(methodName);

                    if (match.Success)
                    {
                        methodName = match.Groups["CompilerGeneratedName"].Value + "()";
                    }
                }

                fullName = methodName;
                methodNameMap.TryAdd(methodName, fullName);
            }

            return fullName;
        }

        /// <summary>
        /// Gets the coverage quota of a class.
        /// This method is used to get coverage quota if line coverage is not available.
        /// </summary>
        /// <param name="methods">The methods.</param>
        /// <returns>The coverage quota.</returns>
        private static decimal? GetCoverageQuotaOfClass(XElement[] methods)
        {
            var methodGroups = methods
                .Where(m => m.Attribute("skippedDueTo") == null && m.Element("FileRef") == null && !m.Element("Name").Value.EndsWith(".ctor()", StringComparison.OrdinalIgnoreCase))
                .GroupBy(m => m.Element("Name").Value)
                .ToArray();

            int visitedMethods = methodGroups.Count(g => g.Any(m => m.Attribute("visited").Value == "true"));

            return (methodGroups.Length == 0) ? (decimal?)null : (decimal)Math.Truncate(1000 * (double)visitedMethods / (double)methodGroups.Length) / 10;
        }
    }
}
