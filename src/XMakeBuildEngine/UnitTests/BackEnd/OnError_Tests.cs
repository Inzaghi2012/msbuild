﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Build.Framework;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using System.Threading;
using System.Collections;
using System.Xml;
using InvalidProjectFileException = Microsoft.Build.Exceptions.InvalidProjectFileException;

namespace Microsoft.Build.UnitTests.BackEnd
{
    /*
     * Class:   OnErrorHandling
     *
     * Tests that exercise the <OnError> tag.
     */
    [TestClass]
    sealed public class OnError_Tests
    {
        /*
         * Method:  Basic
         *
         * Construct a simple OnError tag.
         */
        [TestMethod]
        public void Basic()
        {
            MockLogger l = new MockLogger();
            Project p = new Project(XmlReader.Create(new StringReader(ObjectModelHelpers.CleanupFileContents(@"

                <Project DefaultTargets='Build' ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'> 
                   <Target Name='CleanUp'>
                      <Message Text='CleanUp-was-called.'/>
                   </Target>
                   <Target Name='Build'>
                      <Error Text='This is an error.'/>
                      <OnError ExecuteTargets='CleanUp'/>
                   </Target>
                </Project>"))));

            p.Build(new string[] { "Build" }, new ILogger[] { l });

            Assert.IsTrue(l.ErrorCount == 1, "Expected one error because 'Build' failed.");
            Assert.IsTrue((l.FullLog.IndexOf("CleanUp-was-called") != -1), "The CleanUp target should have been called.");
        }

        /// <summary>
        /// Target items and properties should be published to the project level even when a task that
        /// outputs them fails. (Of course the task must have populated its property before it errors.)
        /// Then these items and properties should be visible to the onerror targets.
        /// </summary>
        [TestMethod]
        public void FailingTaskStillPublishesOutputs()
        {
            MockLogger l = new MockLogger();

            string resx = Path.Combine(Path.GetTempPath(), "FailingTaskStillPublishesOutputs.resx");

            try
            {
                File.WriteAllText(resx, @"
                    <root>
                      <resheader name=""resmimetype"">
                        <value>text/microsoft-resx</value>
                      </resheader>
                      <resheader name=""version"">
                        <value>2.0</value>
                      </resheader>
                      <resheader name=""reader"">
                        <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=2.0.3600.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
                      </resheader>
                      <resheader name=""writer"">
                        <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=2.0.3600.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
                      </resheader>
                      <data name=""a"">
                        <value>aa</value>
                      </data>
                      <data name=""b"">
                        <value>bb</value>
                      </data>
                    </root>");

                Project project = new Project(XmlReader.Create(new StringReader(ObjectModelHelpers.CleanupFileContents(@"
                    <Project DefaultTargets='Build' ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'> 
                        <Target Name='Build'>
                            
                            <GenerateResource
                                ExecuteAsTool='false'
                                Sources='" + resx + @"'
                                StronglyTypedLanguage='!@:|'>
                                    <Output TaskParameter='FilesWritten' ItemName='FilesWrittenItem'/>
                                    <Output TaskParameter='FilesWritten' PropertyName='FilesWrittenProperty'/>
                            </GenerateResource>
                                               
                            <OnError ExecuteTargets='ErrorTarget'/>
                        </Target>

                        <Target Name='ErrorTarget'>    
                            <Message Text='[@(fileswrittenitem)]'/>
                            <Message Text='[$(fileswrittenproperty)]'/>
                        </Target>
                    </Project>"))));

                ProjectInstance p = project.CreateProjectInstance();
                p.Build(new string[] { "Build" }, new ILogger[] { l });

                string resource = Path.ChangeExtension(resx, ".resources");

                Assert.IsTrue(l.ErrorCount == 1, "Expected one error because 'Build' failed.");
                l.AssertLogContains("[" + resource + "]", "[" + resource + "]");

                // And outputs are visible at the project level
                Assert.AreEqual(resource, Helpers.MakeList(p.GetItems("FilesWrittenItem"))[0].EvaluatedInclude);
                Assert.AreEqual(resource, p.GetPropertyValue("FilesWrittenProperty"));

                p = project.CreateProjectInstance();

                // But are gone after resetting of course
                Assert.AreEqual(0, Helpers.MakeList(p.GetItems("FilesWrittenItem")).Count);
                Assert.AreEqual(String.Empty, p.GetPropertyValue("FilesWrittenProperty"));
            }
            finally
            {
                File.Delete(resx);
            }
        }

        /// <summary>
        /// Target items and properties should be published to the project level when an OnError
        /// target runs, and those items and properties should be visible to the OnError targets.
        /// </summary>
        [TestMethod]
        public void OnErrorSeesPropertiesAndItemsFromFirstTarget()
        {
            MockLogger l = new MockLogger();
            Project p = new Project(XmlReader.Create(new StringReader(ObjectModelHelpers.CleanupFileContents(@"

                <Project DefaultTargets='Build' ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'> 

                   <Target Name='Build'>
                      <!-- Create a bunch of items and properties -->
                      <CreateItem Include='a1'>
                        <Output ItemName='i1' TaskParameter='Include'/>
                      </CreateItem> 
                      <ItemGroup>
                        <i1 Include='a2'/>
                      </ItemGroup> 
                      <CreateProperty Value='v1'>
                        <Output PropertyName='p1' TaskParameter='Value'/>
                      </CreateProperty>
                      <PropertyGroup>
                        <p2>v2</p2>
                      </PropertyGroup>

                      <Error Text='This is an error.'/>
                      <OnError ExecuteTargets='ErrorTarget'/>
                   </Target>

                   <Target Name='ErrorTarget'>
                      <Message Text='[@(i1)][$(p1)][$(p2)]'/>
                   </Target>

                </Project>"))));

            p.Build(new string[] { "Build" }, new ILogger[] { l });

            Assert.IsTrue(l.ErrorCount == 1, "Expected one error because 'Build' failed.");
            l.AssertLogContains("[a1;a2][v1][v2]");
        }

        /*
         * Method:  TwoExecuteTargets
         *
         * Make sure two execute targets can be called.
         */
        [TestMethod]
        public void TwoExecuteTargets()
        {
            MockLogger l = new MockLogger();
            Project p = new Project(XmlReader.Create(new StringReader(ObjectModelHelpers.CleanupFileContents(@"

                <Project DefaultTargets='Build' ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'> 
                   <Target Name='CleanUp'>
                      <Message Text='CleanUp-was-called.'/>
                   </Target>
                   <Target Name='CleanUp2'>
                      <Message Text='CleanUp2-was-called.'/>
                   </Target>
                   <Target Name='Build'>
                      <Error Text='This is an error.'/>
                      <OnError ExecuteTargets='CleanUp;CleanUp2'/>
                   </Target>
                </Project>"))));

            p.Build(new string[] { "Build" }, new ILogger[] { l });

            Assert.IsTrue(l.ErrorCount == 1, "Expected one error because 'Build' failed.");
            Assert.IsTrue((l.FullLog.IndexOf("CleanUp-was-called") != -1), "The CleanUp target should have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("CleanUp2-was-called") != -1), "The CleanUp2 target should have been called.");
        }

        /*
         * Method:  TwoOnErrorClauses
         *
         * Make sure two OnError clauses can be used.
         */
        [TestMethod]
        public void TwoOnErrorClauses()
        {
            MockLogger l = new MockLogger();
            Project p = new Project(XmlReader.Create(new StringReader(ObjectModelHelpers.CleanupFileContents(@"

                <Project DefaultTargets='Build' ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'> 
                   <Target Name='CleanUp'>
                      <Message Text='CleanUp-was-called.'/>
                   </Target>
                   <Target Name='CleanUp2'>
                      <Message Text='CleanUp2-was-called.'/>
                   </Target>
                   <Target Name='Build'>
                      <Error Text='This is an error.'/>
                      <OnError ExecuteTargets='CleanUp'/>
                      <OnError ExecuteTargets='CleanUp2'/>
                   </Target>
                </Project>"))));

            p.Build(new string[] { "Build" }, new ILogger[] { l });

            Assert.IsTrue(l.ErrorCount == 1, "Expected one error because 'Build' failed.");
            Assert.IsTrue((l.FullLog.IndexOf("CleanUp-was-called") != -1), "The CleanUp target should have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("CleanUp2-was-called") != -1), "The CleanUp2 target should have been called.");
        }

        /*
         * Method:  DependentTarget
         *
         * Make sure that a target that is a dependent of a target called because of an
         * OnError clause is called
         */
        [TestMethod]
        public void DependentTarget()
        {
            MockLogger l = new MockLogger();
            Project p = new Project(XmlReader.Create(new StringReader(ObjectModelHelpers.CleanupFileContents(@"

                <Project DefaultTargets='Build' ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'> 
                   <Target Name='CleanUp' DependsOnTargets='CleanUp2'>
                      <Message Text='CleanUp-was-called.'/>
                   </Target>
                   <Target Name='CleanUp2'>
                      <Message Text='CleanUp2-was-called.'/>
                   </Target>
                   <Target Name='Build'>
                      <Error Text='This is an error.'/>
                      <OnError ExecuteTargets='CleanUp'/>
                   </Target>
                </Project>"))));

            p.Build(new string[] { "Build" }, new ILogger[] { l });

            Assert.IsTrue(l.ErrorCount == 1, "Expected one error because 'Build' failed.");
            Assert.IsTrue((l.FullLog.IndexOf("CleanUp-was-called") != -1), "The CleanUp target should have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("CleanUp2-was-called") != -1), "The CleanUp2 target should have been called.");
        }

        /*
         * Method:  ErrorInChildIsHandledInParent
         *
         * If a target is dependent on a child target and that child target errors,
         * then the parent's OnError clauses should fire.
         */
        [TestMethod]
        public void ErrorInChildIsHandledInParent()
        {
            MockLogger l = new MockLogger();
            Project p = new Project(XmlReader.Create(new StringReader(ObjectModelHelpers.CleanupFileContents(@"

                <Project DefaultTargets='Build' ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'> 
                   <Target Name='CleanUp'>
                      <Message Text='CleanUp-was-called.'/>
                   </Target>
                   <Target Name='BuildStep1'>
                      <Error Text='Error-in-build-step-1.'/>
                   </Target>
                   <Target Name='Build' DependsOnTargets='BuildStep1'>
                      <OnError ExecuteTargets='CleanUp'/>
                   </Target>
                </Project>"))));

            p.Build(new string[] { "Build" }, new ILogger[] { l });

            Assert.IsTrue(l.ErrorCount == 1, "Expected one error because 'BuildStep1' failed.");
            Assert.IsTrue((l.FullLog.IndexOf("CleanUp-was-called") != -1), "The CleanUp target should have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("Error-in-build-step-1") != -1), "The BuildStep1 target should have been called.");
        }


        /*
         * Method:  NonExistentExecuteTarget
         *
         * Construct a simple OnError tag.
         */
        [TestMethod]
        public void NonExistentExecuteTarget()
        {
            MockLogger l = new MockLogger();
            Project p = new Project(XmlReader.Create(new StringReader(ObjectModelHelpers.CleanupFileContents(@"

                <Project DefaultTargets='Build' ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'> 
                   <Target Name='Build'>
                      <Error Text='This is an error.'/>
                      <OnError ExecuteTargets='CleanUp'/>
                   </Target>
                </Project>"))));

            p.Build(new string[] { "Build" }, new ILogger[] { l });

            Assert.IsTrue(l.ErrorCount == 2, "Expected at least one error because 'Build' failed and one error because 'CleanUp' didn't exist.");
            Assert.IsTrue((l.FullLog.IndexOf("CleanUp-was-called") == -1), "The CleanUp target should not have been called.");
        }

        /*
         * Method:  TrueCondition
         *
         * Test the case when the result of the condition is 'true'
         */
        [TestMethod]
        public void TrueCondition()
        {
            MockLogger l = new MockLogger();
            Project p = new Project(XmlReader.Create(new StringReader(ObjectModelHelpers.CleanupFileContents(@"

                <Project DefaultTargets='Build' ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'> 
                   <Target Name='CleanUp'>
                      <Message Text='CleanUp-was-called.'/>
                   </Target>
                   <Target Name='Build'>
                      <Error Text='This is an error.'/>
                      <OnError Condition=""'A'!='B'"" ExecuteTargets='CleanUp'/>
                   </Target>
                </Project>"))));

            p.Build(new string[] { "Build" }, new ILogger[] { l });

            Assert.IsTrue(l.ErrorCount == 1, "Expected one error because 'Build' failed.");
            Assert.IsTrue((l.FullLog.IndexOf("CleanUp-was-called") != -1), "The CleanUp target should have been called.");
        }

        /*
         * Method:  FalseCondition
         *
         * Test the case when the result of the condition is 'false'
         */
        [TestMethod]
        public void FalseCondition()
        {
            MockLogger l = new MockLogger();
            Project p = new Project(XmlReader.Create(new StringReader(ObjectModelHelpers.CleanupFileContents(@"

                <Project DefaultTargets='Build' ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'> 
                   <Target Name='CleanUp'>
                      <Message Text='CleanUp-was-called.'/>
                   </Target>
                   <Target Name='Build'>
                      <Error Text='This is an error.'/>
                      <OnError Condition=""'A'=='B'"" ExecuteTargets='CleanUp'/>
                   </Target>
                </Project>"))));


            p.Build(new string[] { "Build" }, new ILogger[] { l });

            Assert.IsTrue(l.ErrorCount == 1, "Expected one error because 'Build' failed.");
            Assert.IsTrue((l.FullLog.IndexOf("CleanUp-was-called") == -1), "The CleanUp target should not have been called.");
        }

        /*
         * Method:  PropertiesInExecuteTargets
         *
         * Make sure that properties in ExecuteTargets are properly expanded.
         */
        [TestMethod]
        public void PropertiesInExecuteTargets()
        {
            MockLogger l = new MockLogger();
            Project p = new Project(XmlReader.Create(new StringReader(ObjectModelHelpers.CleanupFileContents(@"

                <Project DefaultTargets='Build' ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'> 
                   <PropertyGroup>
                     <Part1>Clean</Part1>
                     <Part2>Up</Part2>
                   </PropertyGroup>
                   <Target Name='CleanUp'>
                      <Message Text='CleanUp-was-called.'/>
                   </Target>
                   <Target Name='Build'>
                      <Error Text='This is an error.'/>
                      <OnError Condition=""'A'!='B'"" ExecuteTargets='$(Part1)$(Part2)'/>
                   </Target>
                </Project>"))));

            p.Build(new string[] { "Build" }, new ILogger[] { l });

            Assert.IsTrue(l.ErrorCount == 1, "Expected one error because 'Build' failed.");
            Assert.IsTrue((l.FullLog.IndexOf("CleanUp-was-called") != -1), "The CleanUp target should have been called.");
        }

        /*
         * Method:  ErrorTargetsContinueAfterErrorsInErrorHandler
         *
         * If an error occurs in an error handling target, then continue processing
         * remaining error targets
         */
        [TestMethod]
        public void ErrorTargetsContinueAfterErrorsInErrorHandler()
        {
            MockLogger l = new MockLogger();
            Project p = new Project(XmlReader.Create(new StringReader(ObjectModelHelpers.CleanupFileContents(@"

                <Project DefaultTargets='Build' ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'> 
                   <Target Name='CleanUp1'>
                      <Message Text='CleanUp1-was-called.'/>
                      <Error Text='Error in CleanUp1.'/>
                   </Target>
                   <Target Name='CleanUp2'>
                      <Message Text='CleanUp2-was-called.'/>
                      <Error Text='Error in CleanUp2.'/>
                   </Target>
                   <Target Name='CleanUp3'>
                      <Message Text='CleanUp3-was-called.'/>
                      <Error Text='Error in CleanUp3.'/>
                   </Target>
                   <Target Name='CoreBuild'>
                      <Error Text='This is an error.'/>
                      <OnError ExecuteTargets='CleanUp1;CleanUp2'/>
                   </Target>
                   <Target Name='Build' DependsOnTargets='CoreBuild'>
                      <OnError ExecuteTargets='CleanUp3'/>
                   </Target>
                </Project>"))));

            p.Build(new string[] { "Build" }, new ILogger[] { l });

            Assert.IsTrue(l.ErrorCount == 4, "Four build errors expect: One from CoreBuild and on each from the error handlers.");
            Assert.IsTrue((l.FullLog.IndexOf("CleanUp1-was-called") != -1), "The CleanUp1 target should have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("CleanUp2-was-called") != -1), "The CleanUp2 target should have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("CleanUp3-was-called") != -1), "The CleanUp3 target should have been called.");
        }

        /*
         * Method:  ExecuteTargetIsMissing
         *
         * If an OnError specifies an ExecuteTarget that is missing, that's an error
         */
        [TestMethod]
        public void ExecuteTargetIsMissing()
        {
            MockLogger l = new MockLogger();
            Project p = new Project(XmlReader.Create(new StringReader(ObjectModelHelpers.CleanupFileContents(@"

                <Project DefaultTargets='Build' ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'>
                   <Target Name='Build'>
                      <Error Text='This is an error.'/>
                      <OnError Condition=""'A'!='B'"" ExecuteTargets='CleanUp'/>
                   </Target>
                </Project>"))));

            p.Build(new string[] { "Build" }, new ILogger[] { l });

            Assert.IsTrue(l.ErrorCount == 2, "Expected one error because 'Build' failed and one because 'CleanUp' doesn't exist.");
        }

        /*
         * Method:  CommentsAroundOnError
         *
         * Since there is special-case code to ignore comments around OnError blocks,
         * let's test this case.
         */
        [TestMethod]
        public void CommentsAroundOnError()
        {
            MockLogger l = new MockLogger();
            Project p = new Project(XmlReader.Create(new StringReader(ObjectModelHelpers.CleanupFileContents(@"

                <Project DefaultTargets='Build' ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'>
                   <Target Name='CleanUp'>
                      <Message Text='CleanUp-was-called.'/>
                   </Target>
                   <Target Name='Build'>
                      <Error Text='This is an error.'/>
                      <!-- Comment before OnError -->
                      <OnError Condition=""'A'!='B'"" ExecuteTargets='CleanUp'/>
                      <!-- Comment after OnError -->
                   </Target>
                </Project>"))));

            p.Build(new string[] { "Build" }, new ILogger[] { l });

            Assert.IsTrue(l.ErrorCount == 1, "Expected one error because 'Build' failed.");
            Assert.IsTrue((l.FullLog.IndexOf("CleanUp-was-called") != -1), "The CleanUp target should have been called.");
        }

        /*
         * Method:  CircularDependency
         *
         * Detect circular dependencies and break out.
         */
        [TestMethod]
        public void CircularDependency()
        {
            MockLogger l = new MockLogger();
            Project p = new Project(XmlReader.Create(new StringReader(ObjectModelHelpers.CleanupFileContents(@"

                <Project DefaultTargets='Build' ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'>
                   <Target Name='Build'>
                      <Error Text='Error in Build-Target'/>
                      <OnError ExecuteTargets='Build'/>
                   </Target>
                </Project>"))));


            p.Build(new string[] { "Build" }, new ILogger[] { l });

            Assert.IsTrue(l.ErrorCount == 2, "Expected one error because 'Build' failed and one error because of the circular dependency.");
        }

        /*
         * Method:  OutOfOrderOnError
         *
         * OnError clauses must come at the end of a Target, it can't be sprinkled in-between tasks. Catch this case.
         */
        [TestMethod]
        [ExpectedException(typeof(InvalidProjectFileException))]
        public void OutOfOrderOnError()
        {
            MockLogger l = new MockLogger();
            Project p = new Project(XmlReader.Create(new StringReader(ObjectModelHelpers.CleanupFileContents(@"

                <Project DefaultTargets='Build' ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'>
                   <Target Name='CleanUp'>
                      <Message Text='CleanUp-was-called.'/>
                   </Target>
                   <Target Name='Build'>
                      <OnError ExecuteTargets='CleanUp'/>
                      <Delete Files='__non__existent_file__'/>
                   </Target>
                </Project>"))));

            /* No build required */
        }


        #region Postbuild
        /*
         * Method:  PostBuildBasic
         *
         * Handle the basic post-build case where the user has asked for 'On_Success' and
         * none of the build steps fail.
         */
        [TestMethod]
        public void PostBuildBasic()
        {
            MockLogger l = new MockLogger();
            Project p = new Project
            (
                XmlReader.Create(new StringReader(PostBuildBuilder("On_Success", FailAt.Nowhere)))
            );

            p.Build(new string[] { "Build" }, new ILogger[] { l });

            Assert.IsTrue(l.ErrorCount == 0, "Expected no error because 'Build' succeeded.");
            Assert.IsTrue((l.FullLog.IndexOf("ResGen-was-called") != -1), "The ResGen target should have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("Compile-was-called") != -1), "The Compile target should have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("GenerateSatellites-was-called") != -1), "The GenerateSatellites target should have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("PostBuild-was-called") != -1), "The PostBuild target should have been called.");
        }

        /*
         * Method:  PostBuildOnSuccessWhereCompileFailed
         *
         * User asked for 'On_Success' but the compile step failed. We don't expect post-build
         * to be called.
         */
        [TestMethod]
        public void PostBuildOnSuccessWhereCompileFailed()
        {
            MockLogger l = new MockLogger();
            Project p = new Project
            (
                XmlReader.Create(new StringReader(PostBuildBuilder("On_Success", FailAt.Compile)))
            );

            p.Build(new string[] { "Build" }, new ILogger[] { l });

            Assert.IsTrue(l.ErrorCount == 1, "Expected one error because 'Build' failed.");
            Assert.IsTrue((l.FullLog.IndexOf("ResGen-was-called") != -1), "The ResGen target should have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("Compile-was-called") != -1), "The Compile target should have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("Compile-step-failed") != -1), "The Compile target should have failed.");
            Assert.IsTrue((l.FullLog.IndexOf("GenerateSatellites-was-called") == -1), "The GenerateSatellites target should not have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("PostBuild-was-called") == -1), "The PostBuild target should not have been called.");
        }

        /*
         * Method:  PostBuildOnSuccessWhereGenerateSatellitesFailed
         *
         * User asked for 'On_Success' but the PostBuildOnSuccessWhereGenerateSatellitesFailed step
         * failed. We don't expect post-build to be called.
         */
        [TestMethod]
        public void PostBuildOnSuccessWhereGenerateSatellitesFailed()
        {
            MockLogger l = new MockLogger();
            Project p = new Project
            (
                XmlReader.Create(new StringReader(PostBuildBuilder("On_Success", FailAt.GenerateSatellites)))
            );

            p.Build(new string[] { "Build" }, new ILogger[] { l });

            Assert.IsTrue(l.ErrorCount == 1, "Expected one error because 'Build' failed.");
            Assert.IsTrue((l.FullLog.IndexOf("ResGen-was-called") != -1), "The ResGen target should have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("Compile-was-called") != -1), "The Compile target should have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("GenerateSatellites-was-called") != -1), "The GenerateSatellites target should have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("GenerateSatellites-step-failed") != -1), "The GenerateSatellites target should have failed.");
            Assert.IsTrue((l.FullLog.IndexOf("PostBuild-was-called") == -1), "The PostBuild target should not have been called.");
        }

        /*
         * Method:  PostBuildAlwaysWhereCompileFailed
         *
         * User asked for 'Always' but the compile step failed. We expect the post-build
         * to be called.
         */
        [TestMethod]
        public void PostBuildAlwaysWhereCompileFailed()
        {
            MockLogger l = new MockLogger();
            Project p = new Project
            (
                XmlReader.Create(new StringReader(PostBuildBuilder("Always", FailAt.Compile)))
            );

            p.Build(new string[] { "Build" }, new ILogger[] { l });

            Assert.IsTrue(l.ErrorCount == 1, "Expected one error because 'Build' failed.");
            Assert.IsTrue((l.FullLog.IndexOf("ResGen-was-called") != -1), "The ResGen target should have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("Compile-was-called") != -1), "The Compile target should have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("Compile-step-failed") != -1), "The Compile target should have failed.");
            Assert.IsTrue((l.FullLog.IndexOf("GenerateSatellites-was-called") == -1), "The GenerateSatellites target should not have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("PostBuild-was-called") != -1), "The PostBuild target should have been called.");
        }

        /*
         * Method:  PostBuildFinalOutputChangedWhereCompileFailed
         *
         * User asked for 'Final_Output_Changed' but the Compile step failed.
         * We expect post-build to be called.
         */
        [TestMethod]
        public void PostBuildFinalOutputChangedWhereCompileFailed()
        {
            MockLogger l = new MockLogger();
            Project p = new Project
            (
                XmlReader.Create(new StringReader(PostBuildBuilder("Final_Output_Changed", FailAt.Compile)))
            );

            p.Build(new string[] { "Build" }, new ILogger[] { l });

            Assert.IsTrue(l.ErrorCount == 1, "Expected one error because 'Build' failed.");
            Assert.IsTrue((l.FullLog.IndexOf("ResGen-was-called") != -1), "The ResGen target should have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("Compile-was-called") != -1), "The Compile target should have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("Compile-step-failed") != -1), "The Compile target should have failed.");
            Assert.IsTrue((l.FullLog.IndexOf("GenerateSatellites-was-called") == -1), "The GenerateSatellites target should not have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("PostBuild-was-called") == -1), "The PostBuild target should not have been called.");
        }

        /*
         * Method:  PostBuildFinalOutputChangedWhereGenerateSatellitesFailed
         *
         * User asked for 'Final_Output_Changed' but the GenerateSatellites step failed.
         * We expect post-build to be called because Compile succeeded (and wrote to the output).
         */
        [TestMethod]
        public void PostBuildFinalOutputChangedWhereGenerateSatellitesFailed()
        {
            MockLogger l = new MockLogger();
            Project p = new Project
            (
                XmlReader.Create(new StringReader(PostBuildBuilder("Final_Output_Changed", FailAt.GenerateSatellites)))
            );

            p.Build(new string[] { "Build" }, new ILogger[] { l });

            Assert.IsTrue(l.ErrorCount == 1, "Expected one error because 'Build' failed.");
            Assert.IsTrue((l.FullLog.IndexOf("ResGen-was-called") != -1), "The ResGen target should have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("Compile-was-called") != -1), "The Compile target should have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("GenerateSatellites-was-called") != -1), "The GenerateSatellites target should have been called.");
            Assert.IsTrue((l.FullLog.IndexOf("GenerateSatellites-step-failed") != -1), "The GenerateSatellites target should have failed.");
            Assert.IsTrue((l.FullLog.IndexOf("PostBuild-was-called") != -1), "The PostBuild target should have been called.");
        }


        /*
         * The different places that PostBuildBuilder might be instructed to fail at
         */
        private enum FailAt
        {
            Compile,
            GenerateSatellites,
            Nowhere
        }

        /*
         * Method:  PostBuildBuilder
         *
         * Build a project file that mimics the fairly complex way we plan to use OnError
         * to handle all the different combinations of project failures and post-build
         * conditions.
         *
         */
        private static string PostBuildBuilder
        (
            string controlFlag,  // On_Success, Always, Final_Output_Changed
            FailAt failAt
        )
        {
            string compileStep = "";
            if (FailAt.Compile == failAt)
            {
                compileStep = "<Error Text='Compile-step-failed'/>";
            }

            string generateSatellites = "";
            if (FailAt.GenerateSatellites == failAt)
            {
                generateSatellites = "<Error Text='GenerateSatellites-step-failed'/>";
            }

            return String.Format(ObjectModelHelpers.CleanupFileContents(@"
                <Project DefaultTargets='Build' ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'>
                   <PropertyGroup>
                     <Flag>{0}</Flag>
                   </PropertyGroup>
                   <Target Name='ResGen'>
                      <Message Text='ResGen-was-called.'/>
                   </Target>
                   <Target Name='Compile'>
                      <Message Text='Compile-was-called.'/>
                      <RemoveDir Directories='c:\__Hopefully_Nonexistent_Directory__'/>
                      {1}
                      <CreateItem Include='Yes'>
                         <Output TaskParameter='Include' ItemName='FinalOutputChanged'/>
                      </CreateItem>
                   </Target>
                   <Target Name='GenerateSatellites'>
                      <Message Text='GenerateSatellites-was-called.'/>
                      {2}
                   </Target>
                   <Target Name='PostBuild'>
                      <Message Text='PostBuild-was-called.'/>
                   </Target>
                   <Target Name='PostBuildOnSuccess' Condition=""'$(Flag)'!='Final_Output_Changed'"" DependsOnTargets='PostBuild'>
                      <Message Text='PostBuildOnSuccess-was-called.'/>
                   </Target>
                   <Target Name='PostBuildOnOutputChange' Condition=""'$(Flag)_@(FinalOutputChanged)'=='Final_Output_Changed_Yes'"" DependsOnTargets='PostBuild'>
                      <Message Text='PostBuildOnOutputChange-was-called.'/>
                   </Target>
                   <Target Name='CoreBuildSucceeded' DependsOnTargets='PostBuildOnSuccess;PostBuildOnOutputChange'>
                      <Message Text='CoreBuildSucceeded-was-called.'/>
                   </Target>
                   <Target Name='CoreBuild' DependsOnTargets='ResGen;Compile;GenerateSatellites'>
                      <Message Text='CoreBuild-was-called.'/>
                      <OnError Condition=""'$(Flag)'=='Always'"" ExecuteTargets='PostBuild'/>
                      <OnError ExecuteTargets='PostBuildOnOutputChange'/>
                   </Target>
                   <Target Name='Build' DependsOnTargets='CoreBuild;CoreBuildSucceeded'>
                      <Message Text='Build-target-was-called.'/>
                   </Target>
                </Project>
                "), controlFlag, compileStep, generateSatellites);
        }
        #endregion
    }
}