@{
    RootModule = 'CodexWorker.psm1'
    ModuleVersion = '0.1.0'
    GUID = 'e35e66ab-b6b8-4f5c-83e3-0ad9f72b6f86'
    Author = 'Automation Workbench'
    FunctionsToExport = @(
        'Resolve-CodexWorkerPaths',
        'Read-CodexWorkerState',
        'Write-CodexWorkerState',
        'New-CodexIssueAttemptState',
        'Get-CodexIssueAttemptState',
        'Set-CodexIssueAttemptState',
        'Write-CodexIssueAttemptState',
        'Enter-CodexWorkerLock',
        'Exit-CodexWorkerLock',
        'Assert-TrustedGitHubActor',
        'Get-CodexIssueContext',
        'Get-CodexIssueDevelopment',
        'Set-CodexIssueStatus',
        'Add-CodexIssueComment',
        'Get-CodexWorkflowRunUrl',
        'Add-CodexIssueMilestone',
        'Get-CodexPullRequestContext',
        'Get-CodexIssueBranchName',
        'Get-RegisteredWorktrees',
        'Get-OrCreateCodexIssueWorktree',
        'Assert-PathUnderRoot',
        'Initialize-CodexIssueWorktree',
        'Test-CodexWorktreeCleanup',
        'Remove-CodexWorktree'
        ,'Invoke-CodexRun'
        ,'Test-CodexSummary'
        ,'Initialize-CodexResumeCapability'
        ,'Invoke-CodexIssueRun'
        ,'Test-CodexPublication'
        ,'ConvertTo-CodexPullRequestBody'
        ,'Publish-CodexIssue'
        ,'Get-CodexPullRequestForBranch'
        ,'New-CodexDraftPullRequest'
        ,'Set-CodexPullRequestBody'
        ,'Add-CodexPullRequestComment'
        ,'Resolve-CodexPullRequestIssueNumber'
        ,'Resolve-CodexRevisionIssueNumber'
        ,'Invoke-CodexRevision'
        ,'Get-CodexPrerequisitePlan'
        ,'Resolve-CodexRunnerAsset'
        ,'Get-CodexLocalWorkerPlan'
        ,'Invoke-CodexLocalWorkerSetup'
        ,'Test-CodexPrerequisitePolicy'
        ,'Get-CodexVerifiedMasterCommit'
        ,'Assert-CodexCommitReachableFromMaster'
        ,'Register-CodexPendingDeployment'
        ,'Register-CodexPullRequestClosed'
    )
    CmdletsToExport = @()
    VariablesToExport = @()
    AliasesToExport = @()
}
