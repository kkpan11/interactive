﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using FluentAssertions;
using System.Threading.Tasks;
using FluentAssertions.Extensions;
using Microsoft.DotNet.Interactive.Jupyter.Protocol;
using Microsoft.DotNet.Interactive.Tests.Utility;
using Xunit;
using Xunit.Abstractions;
using ZeroMQMessage = Microsoft.DotNet.Interactive.Jupyter.Messaging.Message;

namespace Microsoft.DotNet.Interactive.Jupyter.Tests;

public class InterruptRequestHandlerTests : JupyterRequestHandlerTestBase
{
    public InterruptRequestHandlerTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task sends_InterruptReply()
    {
        var scheduler = CreateScheduler();
        var request = ZeroMQMessage.Create(new InterruptRequest(), null);
        var context = new JupyterRequestContext(JupyterMessageSender, request);

        await scheduler.Schedule(context);

        await context.Done().Timeout(5.Seconds());

        JupyterMessageSender.ReplyMessages
            .Should()
            .ContainSingle(r => r is InterruptReply);
    }
}