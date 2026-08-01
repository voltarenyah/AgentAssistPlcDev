using System;
using Mcp.Engineering.Openness;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Mcp.Engineering.Tests;

public sealed class OpennessErrorMapperTests
{
    [Fact]
    public void NonRecoverableErrorsIncludeRestartRemediation()
    {
        OpennessAssemblyResolver.Register();
        var opennessAssembly = Assembly.LoadFrom(
            @"C:\Program Files\Siemens\Automation\Portal V17\PublicAPI\V17\Siemens.Engineering.dll");
        var exceptionType = opennessAssembly
            .GetType("Siemens.Engineering.NonRecoverableException", throwOnError: true)!;
        var exception = (Exception)FormatterServices.GetUninitializedObject(exceptionType);

        var mapped = OpennessErrorMapper.Map(exception);

        Assert.Equal("NON_RECOVERABLE", mapped.Code);
        Assert.Contains("restart TIA Portal", mapped.Remediation, StringComparison.OrdinalIgnoreCase);
    }
}
