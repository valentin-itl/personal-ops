using JobScout.Contracts;
using JobScout.Worker.Activities;
using System;
using System.Collections.Generic;
using System.Text;
using Temporalio.Workflows;

namespace JobScout.Worker.Workflows
{
    [Workflow]
    public class SourceScanWorkflow
    {
        [WorkflowRun]
        public async Task<SourceScanResult> RunAsync(
            SourceScanRequest request)
        {
            return await Workflow.ExecuteActivityAsync(
                (SourceScanActivities a) =>
                    a.ScanAndPersistAsync(request),
                new()
                {
                    StartToCloseTimeout =
                        TimeSpan.FromMinutes(5)
                });
        }
    }
}
