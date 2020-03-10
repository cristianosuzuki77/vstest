// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.TestPlatform.VsTestConsole.TranslationLayer
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using Microsoft.TestPlatform.VsTestConsole.TranslationLayer.Interfaces;
    using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;
    using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.Interfaces;
    using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.ObjectModel;
    using Microsoft.VisualStudio.TestPlatform.CoreUtilities.Tracing;
    using Microsoft.VisualStudio.TestPlatform.CoreUtilities.Tracing.Interfaces;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client.Interfaces;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;

    using TranslationLayerResources = Microsoft.VisualStudio.TestPlatform.VsTestConsole.TranslationLayer.Resources.Resources;

    /// <summary>
    /// VstestConsoleRequestSender for sending requests to Vstest.console.exe
    /// </summary>
    internal class VsTestConsoleRequestSender : ITranslationLayerRequestSender, ITranslationLayerRequestSenderAsync
    {
        private readonly ICommunicationManager communicationManager;

        private readonly IDataSerializer dataSerializer;
        private readonly ITestPlatformEventSource testPlatformEventSource;

        private readonly ManualResetEvent handShakeComplete = new ManualResetEvent(false);

        private bool handShakeSuccessful = false;

        private int protocolVersion = 2;

        /// <summary>
        /// Use to cancel blocking tasks associated with vstest.console process
        /// </summary>
        private CancellationTokenSource processExitCancellationTokenSource;

        #region Constructor

        public VsTestConsoleRequestSender() : this(new SocketCommunicationManager(), JsonDataSerializer.Instance, TestPlatformEventSource.Instance)
        {
        }

        internal VsTestConsoleRequestSender(ICommunicationManager communicationManager, IDataSerializer dataSerializer, ITestPlatformEventSource testPlatformEventSource)
        {
            this.communicationManager = communicationManager;
            this.dataSerializer = dataSerializer;
            this.testPlatformEventSource = testPlatformEventSource;
        }

        #endregion

        #region ITranslationLayerRequestSender

        /// <summary>
        /// Initializes Communication with vstest.console.exe
        /// Hosts a communication channel and asynchronously connects to vstest.console.exe
        /// </summary>
        /// <returns>Port Number of hosted server on this side</returns>
        public int InitializeCommunication()
        {
            if (EqtTrace.IsInfoEnabled)
            {
                EqtTrace.Info("VsTestConsoleRequestSender.InitializeCommunication: Started.");
            }

            this.processExitCancellationTokenSource = new CancellationTokenSource();
            this.handShakeSuccessful = false;
            this.handShakeComplete.Reset();
            int port = -1;
            try
            {
                port = this.communicationManager.HostServer(new IPEndPoint(IPAddress.Loopback, 0)).Port;
                this.communicationManager.AcceptClientAsync();

                Task.Run(() =>
                {
                    this.communicationManager.WaitForClientConnection(Timeout.Infinite);
                    this.handShakeSuccessful = this.HandShakeWithVsTestConsole();
                    this.handShakeComplete.Set();
                });
            }
            catch (Exception ex)
            {
                EqtTrace.Error("VsTestConsoleRequestSender.InitializeCommunication: Error initializing communication with VstestConsole: {0}", ex);
                this.handShakeComplete.Set();
            }

            if (EqtTrace.IsInfoEnabled)
            {
                EqtTrace.Info("VsTestConsoleRequestSender.InitializeCommunication: Ended.");
            }

            return port;
        }

        /// <summary>
        /// Waits for Vstest.console.exe Connection for a given timeout.
        /// </summary>
        /// <param name="clientConnectionTimeout">Time to wait for the connection</param>
        /// <returns>True, if successful</returns>
        public bool WaitForRequestHandlerConnection(int clientConnectionTimeout)
        {
            var waitSucess = this.handShakeComplete.WaitOne(clientConnectionTimeout);
            return waitSucess && this.handShakeSuccessful;
        }

        /// <inheritdoc/>
        public async Task<int> InitializeCommunicationAsync(int clientConnectionTimeout)
        {
            if (EqtTrace.IsInfoEnabled)
            {
                EqtTrace.Info($"VsTestConsoleRequestSender.InitializeCommunicationAsync: Started with client connection timeout {clientConnectionTimeout} milliseconds.");
            }

            this.processExitCancellationTokenSource = new CancellationTokenSource();
            this.handShakeSuccessful = false;
            this.handShakeComplete.Reset();
            int port = -1;
            try
            {
                port = this.communicationManager.HostServer(new IPEndPoint(IPAddress.Loopback, 0)).Port;
                var timeoutSource = new CancellationTokenSource(clientConnectionTimeout);
                await Task.Run(() => this.communicationManager.AcceptClientAsync(), timeoutSource.Token);

                this.handShakeSuccessful = await this.HandShakeWithVsTestConsoleAsync();
                this.handShakeComplete.Set();
            }
            catch (Exception ex)
            {
                EqtTrace.Error("VsTestConsoleRequestSender.InitializeCommunicationAsync: Error initializing communication with VstestConsole: {0}", ex);
                this.handShakeComplete.Set();
            }

            if (EqtTrace.IsInfoEnabled)
            {
                EqtTrace.Info("VsTestConsoleRequestSender.InitializeCommunicationAsync: Ended.");
            }

            return this.handShakeSuccessful ? port : -1;
        }

        /// <inheritdoc/>
        public void InitializeExtensions(IEnumerable<string> pathToAdditionalExtensions)
        {
            if (EqtTrace.IsInfoEnabled)
            {
                EqtTrace.Info($"VsTestConsoleRequestSender.InitializeExtensions: Initializing extensions with additional extensions path {string.Join(",", pathToAdditionalExtensions.ToList())}.");
            }
            this.communicationManager.SendMessage(MessageType.ExtensionsInitialize, pathToAdditionalExtensions, this.protocolVersion);
        }

        /// <inheritdoc/>
        public void DiscoverTests(IEnumerable<string> sources, string runSettings, TestPlatformOptions options, ITestDiscoveryEventsHandler2 eventHandler)
        {
            if (EqtTrace.IsInfoEnabled)
            {
                EqtTrace.Info("VsTestConsoleRequestSender.DiscoverTests: Starting test discovery.");
            }
            this.SendMessageAndListenAndReportTestCases(sources, runSettings, options, eventHandler);
        }

        /// <summary>
        /// Asynchronous equivalent of <see cref="DiscoverTests"/>.
        /// </summary>
        public async Task DiscoverTestsAsync(IEnumerable<string> sources, string runSettings, TestPlatformOptions options, ITestDiscoveryEventsHandler2 eventHandler)
        {
            if (EqtTrace.IsInfoEnabled)
            {
                EqtTrace.Info("VsTestConsoleRequestSender.DiscoverTestsAsync: Starting test discovery.");
            }
            await this.SendMessageAndListenAndReportTestCasesAsync(sources, runSettings, options, eventHandler);
        }

        /// <inheritdoc/>
        public void StartTestRun(IEnumerable<string> sources, string runSettings, TestPlatformOptions options, ITestRunEventsHandler runEventsHandler)
        {
            if (EqtTrace.IsInfoEnabled)
            {
                EqtTrace.Info("VsTestConsoleRequestSender.StartTestRun: Starting test run.");
            }

            this.SendMessageAndListenAndReportTestResults(
                MessageType.TestRunAllSourcesWithDefaultHost,
                new TestRunRequestPayload() { Sources = sources.ToList(), RunSettings = runSettings, TestPlatformOptions = options },
                runEventsHandler,
                null);
        }

        /// <inheritdoc/>
        public async Task StartTestRunAsync(IEnumerable<string> sources, string runSettings, TestPlatformOptions options, ITestRunEventsHandler runEventsHandler)
        {
            if (EqtTrace.IsInfoEnabled)
            {
                EqtTrace.Info("VsTestConsoleRequestSender.StartTestRunAsync: Starting test run.");
            }
            await this.SendMessageAndListenAndReportTestResultsAsync(
                MessageType.TestRunAllSourcesWithDefaultHost,
                new TestRunRequestPayload() { Sources = sources.ToList(), RunSettings = runSettings, TestPlatformOptions = options },
                runEventsHandler,
                null);
        }

        /// <inheritdoc/>
        public void StartTestRun(IEnumerable<TestCase> testCases, string runSettings, TestPlatformOptions options, ITestRunEventsHandler runEventsHandler)
        {
            if (EqtTrace.IsInfoEnabled)
            {
                EqtTrace.Info("VsTestConsoleRequestSender.StartTestRun: Starting test run.");
            }
            this.SendMessageAndListenAndReportTestResults(
                MessageType.TestRunAllSourcesWithDefaultHost,
                new TestRunRequestPayload() { TestCases = testCases.ToList(), RunSettings = runSettings, TestPlatformOptions = options },
                runEventsHandler,
                null);
        }

        /// <inheritdoc/>
        public async Task StartTestRunAsync(IEnumerable<TestCase> testCases, string runSettings, TestPlatformOptions options, ITestRunEventsHandler runEventsHandler)
        {
            if (EqtTrace.IsInfoEnabled)
            {
                EqtTrace.Info("VsTestConsoleRequestSender.StartTestRunAsync: Starting test run.");
            }

            await this.SendMessageAndListenAndReportTestResultsAsync(
                MessageType.TestRunAllSourcesWithDefaultHost,
                new TestRunRequestPayload() { TestCases = testCases.ToList(), RunSettings = runSettings, TestPlatformOptions = options },
                runEventsHandler,
                null);
        }

        /// <inheritdoc/>
        public void StartTestRunWithCustomHost(
            IEnumerable<string> sources,
            string runSettings,
            TestPlatformOptions options,
            ITestRunEventsHandler runEventsHandler,
            ITestHostLauncher customHostLauncher)
        {
            if (EqtTrace.IsInfoEnabled)
            {
                EqtTrace.Info("VsTestConsoleRequestSender.StartTestRunWithCustomHost: Starting test run.");
            }

            this.SendMessageAndListenAndReportTestResults(
                MessageType.GetTestRunnerProcessStartInfoForRunAll,
                new TestRunRequestPayload()
                {
                    Sources = sources.ToList(),
                    RunSettings = runSettings,
                    DebuggingEnabled = customHostLauncher.IsDebug,
                    TestPlatformOptions = options
                },
                runEventsHandler,
                customHostLauncher);
        }

        /// <inheritdoc/>
        public async Task StartTestRunWithCustomHostAsync(
            IEnumerable<string> sources,
            string runSettings,
            TestPlatformOptions options,
            ITestRunEventsHandler runEventsHandler,
            ITestHostLauncher customHostLauncher)
        {
            if (EqtTrace.IsInfoEnabled)
            {
                EqtTrace.Info("VsTestConsoleRequestSender.StartTestRunWithCustomHostAsync: Starting test run.");
            }

            await this.SendMessageAndListenAndReportTestResultsAsync(
                MessageType.GetTestRunnerProcessStartInfoForRunAll,
                new TestRunRequestPayload()
                {
                    Sources = sources.ToList(),
                    RunSettings = runSettings,
                    DebuggingEnabled = customHostLauncher.IsDebug,
                    TestPlatformOptions = options
                },
                runEventsHandler,
                customHostLauncher);
        }

        /// <inheritdoc/>
        public void StartTestRunWithCustomHost(IEnumerable<TestCase> testCases, string runSettings, TestPlatformOptions options, ITestRunEventsHandler runEventsHandler, ITestHostLauncher customHostLauncher)
        {
            if (EqtTrace.IsInfoEnabled)
            {
                EqtTrace.Info("VsTestConsoleRequestSender.StartTestRunWithCustomHost: Starting test run.");
            }

            this.SendMessageAndListenAndReportTestResults(
                MessageType.GetTestRunnerProcessStartInfoForRunSelected,
                new TestRunRequestPayload
                {
                    TestCases = testCases.ToList(),
                    RunSettings = runSettings,
                    DebuggingEnabled = customHostLauncher.IsDebug,
                    TestPlatformOptions = options
                },
                runEventsHandler,
                customHostLauncher);
        }

        /// <inheritdoc/>
        public async Task StartTestRunWithCustomHostAsync(IEnumerable<TestCase> testCases, string runSettings, TestPlatformOptions options, ITestRunEventsHandler runEventsHandler, ITestHostLauncher customHostLauncher)
        {
            if (EqtTrace.IsInfoEnabled)
            {
                EqtTrace.Info("VsTestConsoleRequestSender.StartTestRunWithCustomHostAsync: Starting test run.");
            }

            await this.SendMessageAndListenAndReportTestResultsAsync(
                MessageType.GetTestRunnerProcessStartInfoForRunSelected,
                new TestRunRequestPayload()
                {
                    TestCases = testCases.ToList(),
                    RunSettings = runSettings,
                    DebuggingEnabled = customHostLauncher.IsDebug,
                    TestPlatformOptions = options
                },
                runEventsHandler,
                customHostLauncher);
        }

        /// <inheritdoc/>
        public void CancelTestRun()
        {
            if (EqtTrace.IsInfoEnabled)
            {
                EqtTrace.Info("VsTestConsoleRequestSender.CancelTestRun: Cancelling test run.");
            }
            this.communicationManager.SendMessage(MessageType.CancelTestRun);
        }

        /// <inheritdoc/>
        public void AbortTestRun()
        {
            if (EqtTrace.IsInfoEnabled)
            {
                EqtTrace.Info("VsTestConsoleRequestSender.AbortTestRun: Aborting test run.");
            }
            this.communicationManager.SendMessage(MessageType.AbortTestRun);
        }

        /// <inheritdoc/>
        public void CancelDiscovery()
        {
            if (EqtTrace.IsInfoEnabled)
            {
                EqtTrace.Info("VsTestConsoleRequestSender.CancelDiscovery: Cancelling test discovery.");
            }
            this.communicationManager.SendMessage(MessageType.CancelDiscovery);
        }

        /// <inheritdoc/>
        public void OnProcessExited()
        {
            this.processExitCancellationTokenSource.Cancel();
        }

        /// <inheritdoc/>
        public void Close()
        {
            this.Dispose();
        }

        /// <summary>
        /// Sends message for terminating the session
        /// </summary>
        public void EndSession()
        {
            this.communicationManager.SendMessage(MessageType.SessionEnd);
        }

        /// <summary>
        /// Closes the communication channel
        /// </summary>
        public void Dispose()
        {
            this.communicationManager?.StopServer();
        }

        #endregion

        private bool HandShakeWithVsTestConsole()
        {
            var success = false;
            var message = this.communicationManager.ReceiveMessage();
            if (message.MessageType == MessageType.SessionConnected)
            {
                this.communicationManager.SendMessage(MessageType.VersionCheck, this.protocolVersion);
                message = this.communicationManager.ReceiveMessage();

                if (message.MessageType == MessageType.VersionCheck)
                {
                    this.protocolVersion = this.dataSerializer.DeserializePayload<int>(message);
                    success = true;
                }
                else if (message.MessageType == MessageType.ProtocolError)
                {
                    // TODO : Payload for ProtocolError needs to finalized.
                    EqtTrace.Error("VsTestConsoleRequestSender.HandShakeWithVsTestConsole: Version Check failed. ProtolError was received from the runner");
                }
                else
                {
                    EqtTrace.Error("VsTestConsoleRequestSender.HandShakeWithVsTestConsole: VersionCheck Message Expected but different message received: Received MessageType: {0}", message.MessageType);
                }
            }
            else
            {
                EqtTrace.Error("VsTestConsoleRequestSender.HandShakeWithVsTestConsole: SessionConnected Message Expected but different message received: Received MessageType: {0}", message.MessageType);
            }

            return success;
        }

        private async Task<bool> HandShakeWithVsTestConsoleAsync()
        {
            var success = false;
            var message = await this.communicationManager.ReceiveMessageAsync(this.processExitCancellationTokenSource.Token);
            if (message.MessageType == MessageType.SessionConnected)
            {
                this.communicationManager.SendMessage(MessageType.VersionCheck, this.protocolVersion);
                message = await this.communicationManager.ReceiveMessageAsync(this.processExitCancellationTokenSource.Token);

                if (message.MessageType == MessageType.VersionCheck)
                {
                    this.protocolVersion = this.dataSerializer.DeserializePayload<int>(message);
                    success = true;
                }
                else if (message.MessageType == MessageType.ProtocolError)
                {
                    // TODO : Payload for ProtocolError needs to finalized.
                    EqtTrace.Error("VsTestConsoleRequestSender.HandShakeWithVsTestConsoleAsync: Version Check failed. ProtolError was revceived from the runner");
                }
                else
                {
                    EqtTrace.Error("VsTestConsoleRequestSender.HandShakeWithVsTestConsoleAsync: VersionCheck Message Expected but different message received: Received MessageType: {0}", message.MessageType);
                }
            }
            else
            {
                EqtTrace.Error("VsTestConsoleRequestSender.HandShakeWithVsTestConsoleAsync: SessionConnected Message Expected but different message received: Received MessageType: {0}", message.MessageType);
            }

            return success;
        }

        private void SendMessageAndListenAndReportTestCases(IEnumerable<string> sources, string runSettings, TestPlatformOptions options, ITestDiscoveryEventsHandler2 eventHandler)
        {
            try
            {
                this.communicationManager.SendMessage(
                            MessageType.StartDiscovery,
                            new DiscoveryRequestPayload() { Sources = sources, RunSettings = runSettings, TestPlatformOptions = options },
                            this.protocolVersion);
                var isDiscoveryComplete = false;

                // Cycle through the messages that the vstest.console sends.
                // Currently each of the operations are not separate tasks since they should not each take much time.
                // This is just a notification.
                while (!isDiscoveryComplete)
                {
                    var message = this.TryReceiveMessage();

                    if (string.Equals(MessageType.TestCasesFound, message.MessageType))
                    {
                        var testCases = this.dataSerializer.DeserializePayload<IEnumerable<TestCase>>(message);

                        eventHandler.HandleDiscoveredTests(testCases);
                    }
                    else if (string.Equals(MessageType.DiscoveryComplete, message.MessageType))
                    {
                        if (EqtTrace.IsInfoEnabled)
                        {
                            EqtTrace.Info("VsTestConsoleRequestSender.SendMessageAndListenAndReportTestCases: Discovery complete.");
                        }

                        var discoveryCompletePayload =
                            this.dataSerializer.DeserializePayload<DiscoveryCompletePayload>(message);

                        var discoveryCompleteEventArgs = new DiscoveryCompleteEventArgs(discoveryCompletePayload.TotalTests, discoveryCompletePayload.IsAborted);

                        // Adding Metrics From VsTestConsole
                        discoveryCompleteEventArgs.Metrics = discoveryCompletePayload.Metrics;

                        eventHandler.HandleDiscoveryComplete(
                            discoveryCompleteEventArgs,
                            discoveryCompletePayload.LastDiscoveredTests);
                        isDiscoveryComplete = true;
                    }
                    else if (string.Equals(MessageType.TestMessage, message.MessageType))
                    {
                        var testMessagePayload = this.dataSerializer.DeserializePayload<TestMessagePayload>(message);
                        eventHandler.HandleLogMessage(testMessagePayload.MessageLevel, testMessagePayload.Message);
                    }
                }
            }
            catch (Exception exception)
            {
                EqtTrace.Error("Aborting Test Discovery Operation: {0}", exception);
                eventHandler.HandleLogMessage(TestMessageLevel.Error, TranslationLayerResources.AbortedTestsDiscovery);
                var discoveryCompleteEventArgs = new DiscoveryCompleteEventArgs(-1, true);
                eventHandler.HandleDiscoveryComplete(discoveryCompleteEventArgs, null);

                // Earlier we were closing the connection with vstest.console in case of exceptions
                // Removing that code because vstest.console might be in a healthy state and letting the client
                // know of the error, so that the TL can wait for the next instruction from the client itself.
                // Also, connection termination might not kill the process which could result in files being locked by testhost.
            }

            this.testPlatformEventSource.TranslationLayerDiscoveryStop();
        }

        private async Task SendMessageAndListenAndReportTestCasesAsync(IEnumerable<string> sources, string runSettings, TestPlatformOptions options, ITestDiscoveryEventsHandler2 eventHandler)
        {
            try
            {
                this.communicationManager.SendMessage(
                            MessageType.StartDiscovery,
                            new DiscoveryRequestPayload() { Sources = sources, RunSettings = runSettings, TestPlatformOptions = options },
                            this.protocolVersion);
                var isDiscoveryComplete = false;

                // Cycle through the messages that the vstest.console sends.
                // Currently each of the operations are not separate tasks since they should not each take much time.
                // This is just a notification.
                while (!isDiscoveryComplete)
                {
                    var message = await this.TryReceiveMessageAsync();

                    if (string.Equals(MessageType.TestCasesFound, message.MessageType))
                    {
                        var testCases = this.dataSerializer.DeserializePayload<IEnumerable<TestCase>>(message);

                        eventHandler.HandleDiscoveredTests(testCases);
                    }
                    else if (string.Equals(MessageType.DiscoveryComplete, message.MessageType))
                    {
                        if (EqtTrace.IsInfoEnabled)
                        {
                            EqtTrace.Info("VsTestConsoleRequestSender.SendMessageAndListenAndReportTestCasesAsync: Discovery complete.");
                        }

                        var discoveryCompletePayload =
                            this.dataSerializer.DeserializePayload<DiscoveryCompletePayload>(message);

                        var discoveryCompleteEventArgs = new DiscoveryCompleteEventArgs(discoveryCompletePayload.TotalTests, discoveryCompletePayload.IsAborted);

                        // Adding Metrics from VsTestConsole
                        discoveryCompleteEventArgs.Metrics = discoveryCompletePayload.Metrics;

                        eventHandler.HandleDiscoveryComplete(
                            discoveryCompleteEventArgs,
                            discoveryCompletePayload.LastDiscoveredTests);
                        isDiscoveryComplete = true;
                    }
                    else if (string.Equals(MessageType.TestMessage, message.MessageType))
                    {
                        var testMessagePayload = this.dataSerializer.DeserializePayload<TestMessagePayload>(message);
                        eventHandler.HandleLogMessage(testMessagePayload.MessageLevel, testMessagePayload.Message);
                    }
                }
            }
            catch (Exception exception)
            {
                EqtTrace.Error("Aborting Test Discovery Operation: {0}", exception);

                eventHandler.HandleLogMessage(TestMessageLevel.Error, TranslationLayerResources.AbortedTestsDiscovery);

                var discoveryCompleteEventArgs = new DiscoveryCompleteEventArgs(-1, true);
                eventHandler.HandleDiscoveryComplete(discoveryCompleteEventArgs, null);

                // Earlier we were closing the connection with vstest.console in case of exceptions
                // Removing that code because vstest.console might be in a healthy state and letting the client
                // know of the error, so that the TL can wait for the next instruction from the client itself.
                // Also, connection termination might not kill the process which could result in files being locked by testhost.
            }

            this.testPlatformEventSource.TranslationLayerDiscoveryStop();
        }

        private void SendMessageAndListenAndReportTestResults(string messageType, object payload, ITestRunEventsHandler eventHandler, ITestHostLauncher customHostLauncher)
        {
            try
            {
                this.communicationManager.SendMessage(messageType, payload, this.protocolVersion);
                var isTestRunComplete = false;

                // Cycle through the messages that the testhost sends. 
                // Currently each of the operations are not separate tasks since they should not each take much time. This is just a notification.
                while (!isTestRunComplete)
                {
                    var message = this.TryReceiveMessage();

                    if (string.Equals(MessageType.TestRunStatsChange, message.MessageType))
                    {
                        var testRunChangedArgs = this.dataSerializer.DeserializePayload<TestRunChangedEventArgs>(
                            message);
                        eventHandler.HandleTestRunStatsChange(testRunChangedArgs);
                    }
                    else if (string.Equals(MessageType.ExecutionComplete, message.MessageType))
                    {
                        if (EqtTrace.IsInfoEnabled)
                        {
                            EqtTrace.Info("VsTestConsoleRequestSender.SendMessageAndListenAndReportTestResults: Execution complete.");
                        }

                        var testRunCompletePayload =
                            this.dataSerializer.DeserializePayload<TestRunCompletePayload>(message);

                        eventHandler.HandleTestRunComplete(
                            testRunCompletePayload.TestRunCompleteArgs,
                            testRunCompletePayload.LastRunTests,
                            testRunCompletePayload.RunAttachments,
                            testRunCompletePayload.ExecutorUris);
                        isTestRunComplete = true;
                    }
                    else if (string.Equals(MessageType.TestMessage, message.MessageType))
                    {
                        var testMessagePayload = this.dataSerializer.DeserializePayload<TestMessagePayload>(message);
                        eventHandler.HandleLogMessage(testMessagePayload.MessageLevel, testMessagePayload.Message);
                    }
                    else if (string.Equals(MessageType.CustomTestHostLaunch, message.MessageType))
                    {
                        HandleCustomHostLaunch(customHostLauncher, message);
                    }
                    else if (string.Equals(MessageType.ProxyAttachDebuggerToProcess, message.MessageType))
                    {
                        AttachDebuggerToProcess(customHostLauncher, message);
                    }
                }
            }
            catch (Exception exception)
            {
                EqtTrace.Error("Aborting Test Run Operation: {0}", exception);
                eventHandler.HandleLogMessage(TestMessageLevel.Error, TranslationLayerResources.AbortedTestsRun);
                var completeArgs = new TestRunCompleteEventArgs(null, false, true, exception, null, TimeSpan.Zero);
                eventHandler.HandleTestRunComplete(completeArgs, null, null, null);

                // Earlier we were closing the connection with vstest.console in case of exceptions
                // Removing that code because vstest.console might be in a healthy state and letting the client
                // know of the error, so that the TL can wait for the next instruction from the client itself.
                // Also, connection termination might not kill the process which could result in files being locked by testhost.
            }

            this.testPlatformEventSource.TranslationLayerExecutionStop();
        }

        private async Task SendMessageAndListenAndReportTestResultsAsync(string messageType, object payload, ITestRunEventsHandler eventHandler, ITestHostLauncher customHostLauncher)
        {
            try
            {
                this.communicationManager.SendMessage(messageType, payload, this.protocolVersion);
                var isTestRunComplete = false;

                // Cycle through the messages that the testhost sends. 
                // Currently each of the operations are not separate tasks since they should not each take much time. This is just a notification.
                while (!isTestRunComplete)
                {
                    var message = await this.TryReceiveMessageAsync();

                    if (string.Equals(MessageType.TestRunStatsChange, message.MessageType))
                    {
                        var testRunChangedArgs = this.dataSerializer.DeserializePayload<TestRunChangedEventArgs>(
                            message);
                        eventHandler.HandleTestRunStatsChange(testRunChangedArgs);
                    }
                    else if (string.Equals(MessageType.ExecutionComplete, message.MessageType))
                    {
                        if (EqtTrace.IsInfoEnabled)
                        {
                            EqtTrace.Info("VsTestConsoleRequestSender.SendMessageAndListenAndReportTestResultsAsync: Execution complete.");
                        }

                        var testRunCompletePayload =
                            this.dataSerializer.DeserializePayload<TestRunCompletePayload>(message);

                        eventHandler.HandleTestRunComplete(
                            testRunCompletePayload.TestRunCompleteArgs,
                            testRunCompletePayload.LastRunTests,
                            testRunCompletePayload.RunAttachments,
                            testRunCompletePayload.ExecutorUris);
                        isTestRunComplete = true;
                    }
                    else if (string.Equals(MessageType.TestMessage, message.MessageType))
                    {
                        var testMessagePayload = this.dataSerializer.DeserializePayload<TestMessagePayload>(message);
                        eventHandler.HandleLogMessage(testMessagePayload.MessageLevel, testMessagePayload.Message);
                    }
                    else if (string.Equals(MessageType.CustomTestHostLaunch, message.MessageType))
                    {
                        HandleCustomHostLaunch(customHostLauncher, message);
                    }
                    else if (string.Equals(MessageType.ProxyAttachDebuggerToProcess, message.MessageType))
                    {
                        AttachDebuggerToProcess(customHostLauncher, message);
                    }
                }
            }
            catch (Exception exception)
            {
                EqtTrace.Error("Aborting Test Run Operation: {0}", exception);
                eventHandler.HandleLogMessage(TestMessageLevel.Error, TranslationLayerResources.AbortedTestsRun);
                var completeArgs = new TestRunCompleteEventArgs(null, false, true, exception, null, TimeSpan.Zero);
                eventHandler.HandleTestRunComplete(completeArgs, null, null, null);

                // Earlier we were closing the connection with vstest.console in case of exceptions
                // Removing that code because vstest.console might be in a healthy state and letting the client
                // know of the error, so that the TL can wait for the next instruction from the client itself.
                // Also, connection termination might not kill the process which could result in files being locked by testhost.
            }

            this.testPlatformEventSource.TranslationLayerExecutionStop();
        }

        private Message TryReceiveMessage()
        {
            Message message = null;
            var receiverMessageTask = this.communicationManager.ReceiveMessageAsync(this.processExitCancellationTokenSource.Token);
            receiverMessageTask.Wait();
            message = receiverMessageTask.Result;

            if (message == null)
            {
                throw new TransationLayerException(TranslationLayerResources.FailedToReceiveMessage);
            }

            return message;
        }

        private async Task<Message> TryReceiveMessageAsync()
        {
            Message message = await this.communicationManager.ReceiveMessageAsync(this.processExitCancellationTokenSource.Token);

            if (message == null)
            {
                throw new TransationLayerException(TranslationLayerResources.FailedToReceiveMessage);
            }

            return message;
        }

        private void HandleCustomHostLaunch(ITestHostLauncher customHostLauncher, Message message)
        {
            var ackPayload = new CustomHostLaunchAckPayload() { HostProcessId = -1, ErrorMessage = null };

            try
            {
                var testProcessStartInfo = this.dataSerializer.DeserializePayload<TestProcessStartInfo>(message);

                ackPayload.HostProcessId = customHostLauncher != null
                                    ? customHostLauncher.LaunchTestHost(testProcessStartInfo)
                                    : -1;
            }
            catch (Exception ex)
            {
                EqtTrace.Error("Error while launching custom host: {0}", ex);

                // Vstest.console will send the abort message properly while cleaning up all the flow, so do not abort here
                // Let the ack go through and let vstest.console handle the error
                ackPayload.ErrorMessage = ex.Message;
            }
            finally
            {
                // Always unblock the Vstest.console thread which is indefintitely waiting on this ACK
                this.communicationManager.SendMessage(MessageType.CustomTestHostLaunchCallback, ackPayload, this.protocolVersion);
            }
        }

        private void AttachDebuggerToProcess(ITestHostLauncher customHostLauncher, Message message)
        {
            if (customHostLauncher == null)
            {
                throw new ArgumentNullException("VsTestConsoleRequestSender.AttachDebuggerToProcess: test host launcher cannot be null.");
            }
            if (!(customHostLauncher is IDebugTestHostLauncher launcher))
            {
                throw new NotSupportedException("VsTestConsoleRequestSender.AttachDebuggerToProcess: test host launcher is not ITestHostLauncher2.");
            }

            bool response = false;
            try
            {
                var pid = this.dataSerializer.DeserializePayload<int>(message);
                response = launcher.AttachDebuggerToProcess(pid);

            }
            catch (Exception ex)
            {
                EqtTrace.Error("VsTestConsoleRequestSender.AttachDebuggerToProcess: Error while attaching debugger to process: {0}", ex);
            }
            finally
            {
                this.communicationManager.SendMessage(MessageType.ProxyAttachDebuggerToProcessCallback, response, this.protocolVersion);
            }
        }
    }
}
