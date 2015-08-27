﻿using System.IO;
using System.Reflection;
using System.Web;

namespace ReportUnit.Parser
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Xml;
    using Layer;
    using Logging;
    using Support;

    internal class MsTest2010 : IParser
    {
        /// <summary>
        /// XmlDocument instance
        /// </summary>
        private XmlDocument _doc;

        /// <summary>
        /// The input file from MS Test TestResult.xml
        /// </summary>
		private string _testResultFile = "";

		/// <summary>
		/// Usually evaluates to the assembly name. Used to clean up test names so its easier to read in the outputted html.
		/// </summary>
		private string _fileNameWithoutExtension;

        /// <summary>
        /// Contains report level data to be passed to the Folder level report to build summary
        /// </summary>
        private Report _report;

        /// <summary>
        /// Xml namespace for processing file
        /// </summary>
        private XmlNamespaceManager _nsmgr;

        /// <summary>
        /// Logger
        /// </summary>
        Logger logger = Logger.GetLogger();

        public IParser LoadFile(string testResultFile)
        {
            if (_doc == null) _doc = new XmlDocument();

            _testResultFile = testResultFile;

            _doc.Load(testResultFile);

            _nsmgr = new XmlNamespaceManager(_doc.NameTable);
            _nsmgr.AddNamespace("t", "http://microsoft.com/schemas/VisualStudio/TeamTest/2010");

            return this;
        }

        public Report ProcessFile()
		{
			_fileNameWithoutExtension = Path.GetFileNameWithoutExtension(this._testResultFile);

            // create a data instance to be passed to the folder level report
            _report = new Report();
            _report.FileName = this._testResultFile;
            _report.RunInfo.TestRunner = TestRunner.MSTest2010;

            // get total count of tests from the input file
            _report.Total = _doc.SelectNodes("descendant::t:UnitTestResult", _nsmgr).Count;

            logger.Info("[Number of tests: " + _report.Total);

            // only proceed if the test count is more than 0
            if (_report.Total >= 1)
            {
                logger.Info("[Processing root and test-suite elements...");

                // pull values from XML source
                _report.AssemblyName = _doc.GetElementsByTagName("UnitTest")[0]["TestMethod"].Attributes["codeBase"].InnerText;

                _report.Passed = _doc.SelectNodes("descendant::t:UnitTestResult[@outcome='Passed']", _nsmgr).Count;
                _report.Failed = _doc.SelectNodes("descendant::t:UnitTestResult[@outcome='Failed']", _nsmgr).Count;
                _report.Inconclusive = _doc.SelectNodes("descendant::t:UnitTestResult[@outcome='Inconclusive' or @outcome='notRunnable' or @outcome='passedButRunAborted' or @outcome='disconnected' or @outcome='warning' or @outcome='pending']", _nsmgr).Count;
                _report.Skipped = _doc.SelectNodes("descendant::t:UnitTestResult[@outcome='NotExecuted']", _nsmgr).Count;
                _report.Errors = _doc.SelectNodes("descendant::t:UnitTestResult[@outcome='Error' or @outcome='Aborted' or @outcome='timeout']", _nsmgr).Count;

                try
                {
                    XmlNode times = _doc.SelectSingleNode("descendant::t:Times", _nsmgr);
                    if (times != null) _report.Duration = DateTimeHelper.DifferenceInMilliseconds(times.Attributes["start"].InnerText, times.Attributes["finish"].InnerText);
                }
                catch { }

                ProcessRunInfo();
                ProcessFixtureBlocks();

                _report.Status = ReportHelper.GetFixtureStatus(_report.TestFixtures.SelectMany(tf => tf.Tests).ToList());
            }
            else
            {
                try
                {
                    _report.Status = Status.Passed;

                    return _report;
                }
                catch (Exception ex)
                {
                    logger.Fatal("Something weird happened: " + ex.Message);
                    return null;
                }
            }

            return _report;
        }


        /// <summary>
        /// Find meta information about the whole test run
        /// </summary>
        private void ProcessRunInfo()
        {
            _report.RunInfo.Info.Add("TestResult File", _testResultFile);

            try
            {
                DateTime lastModified = System.IO.File.GetLastWriteTime(_testResultFile);
                _report.RunInfo.Info.Add("Last Run", lastModified.ToString("d MMM yyyy HH:mm"));
            }
            catch (Exception) { }

            if (_report.Duration > 0) _report.RunInfo.Info.Add("Duration", string.Format("{0} ms", _report.Duration));

            try
            {
                // try to parse the TestRun node
                XmlNode testRun = _doc.SelectSingleNode("descendant::t:TestRun", _nsmgr);

                if (testRun != null)
                {
                    _report.RunInfo.Info.Add("Machine Name", _doc.SelectNodes("descendant::t:UnitTestResult", _nsmgr)[0].Attributes["computerName"].InnerText);
                    _report.RunInfo.Info.Add("TestRunner", _report.RunInfo.TestRunner.ToString());
                    _report.RunInfo.Info.Add("TestRunner Version", testRun.Attributes["xmlns"].InnerText);


                    var userInfo = testRun.Attributes["runUser"].InnerText;
                    if (!string.IsNullOrWhiteSpace(userInfo))
                    {
                        _report.RunInfo.Info.Add("User", userInfo.Split('\\').Last());
                        _report.RunInfo.Info.Add("User Domain", userInfo.Split('\\').First());
                    }
                }
                else
                {
                    _report.RunInfo.Info.Add("TestRunner", _report.RunInfo.TestRunner.ToString());
                }
            }
            catch (Exception ex)
            {
                _report.RunInfo.Info.Add("TestRunner", _report.RunInfo.TestRunner.ToString());
                logger.Error("There was an error processing the _ENVIRONMENT_ node: " + ex.Message);
            }
        }


        /// <summary>
        /// Processes the tests level blocks
        /// Adds all tests to the output
        /// </summary>
        private void ProcessFixtureBlocks()
        {
            Console.WriteLine("[INFO] Building fixture blocks...");

            int testCount = 0;
            var unitTestResults = _doc.SelectNodes("descendant::t:UnitTestResult", _nsmgr);

            // run for each test-suite
            foreach (XmlNode testResult in unitTestResults)
            {
                Test tc = new Test();
				tc.Name = testResult.Attributes["testName"].InnerText.Replace(_fileNameWithoutExtension + ".", "");
                tc.Status = testResult.Attributes["outcome"].InnerText.AsStatus();

                if (testResult.Attributes["duration"] != null)
                {
                    TimeSpan d;
                    var durationTimeSpan = testResult.Attributes["duration"].InnerText;
                    if (TimeSpan.TryParse(durationTimeSpan, out d))
                    {
                        tc.Duration = d.TotalMilliseconds;
                    }
                }
                else if (testResult.Attributes["startTime"] != null && testResult.Attributes["endTime"] != null)
                {
                    tc.Duration = DateTimeHelper.DifferenceInMilliseconds(testResult.Attributes["startTime"].InnerText, testResult.Attributes["endTime"].InnerText);
                }

                // check for any errors or messages
                if (testResult.HasChildNodes)
                {
                    string errorMsg = "", descMsg = "", traceMsg = "";
                    foreach (XmlNode node in testResult.ChildNodes)
                    {
                        if (node.Name.Equals("Output", StringComparison.CurrentCultureIgnoreCase) && node.HasChildNodes)
                        {
                            foreach (XmlNode msgNode in node.ChildNodes)
                            {
                                if (msgNode.Name.Equals("ErrorInfo", StringComparison.CurrentCultureIgnoreCase) && msgNode.HasChildNodes)
                                {
                                    errorMsg = msgNode["Message"] != null ? "<pre>" + msgNode["Message"].InnerText : "";
                                    errorMsg += msgNode["StackTrace"] != null ? msgNode["StackTrace"].InnerText.Replace("\r", "").Replace("\n", "") : "";
                                    errorMsg += "</pre>";
                                    errorMsg = errorMsg == "<pre></pre>" ? "" : errorMsg;
                                }
                                else if (msgNode.Name.Equals("StdOut", StringComparison.CurrentCultureIgnoreCase))
                                {
                                    descMsg += "<p class='description'>Description: " + msgNode.InnerText;
                                    descMsg += "</p>";
                                    descMsg = descMsg == "<p class='description'>Description: </p>" ? "" : descMsg;
                                }
                                else if (msgNode.Name.Equals("DebugTrace", StringComparison.CurrentCultureIgnoreCase))
                                {
                                    traceMsg += "<pre>" + HttpUtility.HtmlEncode( msgNode.InnerText) + "</pre>";
                                }
                            }
                        }
                    }
                    tc.StatusMessage = descMsg + errorMsg + traceMsg;
                }

                // get test details and fixture
                string testId = testResult.Attributes["testId"].InnerText;
                var testDefinition = _doc.SelectSingleNode("descendant::t:UnitTest[@id='" + testId + "']/t:TestMethod", _nsmgr);
				var className = FixtureName(testDefinition.Attributes["className"].InnerText);

                // get the test fixture details
                var testFixture = _report.TestFixtures.SingleOrDefault(f => f.Name.Equals(className, StringComparison.CurrentCultureIgnoreCase));
                if (testFixture == null)
                {
                    testFixture = new TestSuite();
					testFixture.Name = className;

                    _report.TestFixtures.Add(testFixture);
                }

                // update test fixture with details from the test
                testFixture.Duration += tc.Duration;
                testFixture.Status = ReportHelper.GetFixtureStatus(new List<Status> { testFixture.Status, tc.Status });
                testFixture.Tests.Add(tc);

                Console.Write("\r{0} tests processed...", ++testCount);
            }
        }

        /// <summary>
        /// Discerns a fixture name from a className, removing likely redundant namespaces
        /// </summary>
        /// <param name="className">A class name that's either an ordinary name or a fully qualified name</param>
        /// <returns>A simpler fixture name</returns>
        private string FixtureName(string className)
        {
            if (className == null) return String.Empty;

            string typeName;
            string assemblyName = String.Empty;

            // Remove any assembly properties of the form "property=value" off the end
            var likelyAssemblyNameFirst = className.Split(',').Reverse().SkipWhile(x => x.Contains('=')).ToList();
            // the remainder is either "typename, assemblyname" or something like typename<T1,T2>
            if (likelyAssemblyNameFirst.Count > 1 && !likelyAssemblyNameFirst[0].Contains('>'))
            {
                assemblyName = likelyAssemblyNameFirst[0].Trim();
                typeName = String.Join(",", likelyAssemblyNameFirst.Skip(1).Reverse());
            }
            else
            {
                typeName = String.Join(",", ((IEnumerable<string>) likelyAssemblyNameFirst).Reverse());
            }

            // Remove a starting "assemblyname." or everything up through a ".assemblyname."
            var shortenedNames =
                new[] {_fileNameWithoutExtension, assemblyName}.Select(
                    namePrefix =>
                        string.IsNullOrEmpty(namePrefix)
                            ? typeName
                            : typeName.StartsWith(namePrefix + ".")
                                ? typeName.Substring(namePrefix.Length + 1)
                                : typeName.Split(new[] {"." + namePrefix + "."}, 2, StringSplitOptions.None).Last());
            return shortenedNames.OrderBy(x => x.Length).First();
        }
    }
}
