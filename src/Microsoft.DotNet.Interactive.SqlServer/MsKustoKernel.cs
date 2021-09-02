﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.DotNet.Interactive.ExtensionLab;
using Microsoft.DotNet.Interactive.Formatting;
using Microsoft.DotNet.Interactive.Formatting.TabularData;

namespace Microsoft.DotNet.Interactive.SqlServer
{
    public class MsKustoKernel :
        Kernel,
        IKernelCommandHandler<SubmitCode>,
        IKernelCommandHandler<RequestCompletions>
    {
        private bool _connected;
        private bool _intellisenseReady;
        private readonly Uri _tempFileUri;
        private readonly KustoConnectionDetails _connectionDetails;
        private readonly MsSqlServiceClient _serviceClient;

        private readonly TaskCompletionSource<ConnectionCompleteParams> _connectionCompleted = new();

        private Func<QueryCompleteParams, Task> _queryCompletionHandler;
        private Func<MessageParams, Task> _queryMessageHandler;

        public MsKustoKernel(
            string name,
            KustoConnectionDetails connectionDetails,
            MsSqlServiceClient client) : base(name)
        {
            if (connectionDetails is null)
            {
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(connectionDetails));
            }

            var filePath = Path.GetTempFileName();
            _tempFileUri = new Uri(filePath);
            _connectionDetails = connectionDetails;

            _serviceClient = client ?? throw new ArgumentNullException(nameof(client));
            _serviceClient.Initialize();

            _serviceClient.OnConnectionComplete += HandleConnectionComplete;
            _serviceClient.OnQueryComplete += HandleQueryComplete;
            _serviceClient.OnIntellisenseReady += HandleIntellisenseReady;
            _serviceClient.OnQueryMessage += HandleQueryMessage;

            RegisterForDisposal(() =>
            {
                if (_connected)
                {
                    Task.Run(() => _serviceClient.DisconnectAsync(_tempFileUri)).Wait();
                }
            });
            RegisterForDisposal(() => File.Delete(_tempFileUri.LocalPath));
        }

        private void HandleConnectionComplete(object sender, ConnectionCompleteParams connParams)
        {
            if (connParams.OwnerUri.Equals(_tempFileUri.AbsolutePath))
            {
                if (connParams.ErrorMessage is not null)
                {
                    _connectionCompleted.SetException(new Exception(connParams.ErrorMessage));
                }
                else
                {
                    _connectionCompleted.SetResult(connParams);
                }
            }
        }

        private void HandleQueryComplete(object sender, QueryCompleteParams queryParams)
        {
            if (_queryCompletionHandler is not null)
            {
                Task.Run(() => _queryCompletionHandler(queryParams)).Wait();
            }
        }

        private void HandleQueryMessage(object sender, MessageParams messageParams)
        {
            if (_queryMessageHandler is not null)
            {
                Task.Run(() => _queryMessageHandler(messageParams)).Wait();
            }
        }

        private void HandleIntellisenseReady(object sender, IntelliSenseReadyParams readyParams)
        {
            if (readyParams.OwnerUri.Equals(_tempFileUri.AbsolutePath))
            {
                _intellisenseReady = true;
            }
        }

        public async Task ConnectAsync()
        {
            if (!_connected)
            {
                await _serviceClient.ConnectAsync(_tempFileUri, _connectionDetails);
                await _connectionCompleted.Task;
                _connected = true;
            }
        }

        public async Task HandleAsync(SubmitCode command, KernelInvocationContext context)
        {
            if (!_connected)
            {
                return;
            }

            // If a query handler is already defined, then it means another query is already running in parallel.
            // We only want to run one query at a time, so we display an error here instead.
            if (_queryCompletionHandler is not null)
            {
                context.Display("Error: Another query is currently running. Please wait for that query to complete before re-running this cell.");
                return;
            }

            var completion = new TaskCompletionSource<bool>();

            _queryCompletionHandler = async queryParams =>
            {
                try
                {
                    foreach (var batchSummary in queryParams.BatchSummaries)
                    {
                        foreach (var resultSummary in batchSummary.ResultSetSummaries)
                        {
                            if (completion.Task.IsCompleted)
                            {
                                return;
                            }

                            if (resultSummary.RowCount > 0)
                            {
                                var subsetParams = new QueryExecuteSubsetParams
                                {
                                    OwnerUri = _tempFileUri.AbsolutePath,
                                    BatchIndex = batchSummary.Id,
                                    ResultSetIndex = resultSummary.Id,
                                    RowsStartIndex = 0,
                                    RowsCount = Convert.ToInt32(resultSummary.RowCount)
                                };
                                var subsetResult = await _serviceClient.ExecuteQueryExecuteSubsetAsync(subsetParams);
                                var tables = GetEnumerableTables(resultSummary.ColumnInfo, subsetResult.ResultSubset.Rows);
                                foreach (var table in tables)
                                {
                                    var explorer = new NteractDataExplorer(table.ToTabularDataResource());
                                    context.Display(explorer);
                                }
                            }
                            else
                            {
                                context.Display($"Info: No rows were returned for query {resultSummary.Id} in batch {batchSummary.Id}.");
                            }
                        }
                    }

                    completion.SetResult(true);
                }
                catch (Exception e)
                {
                    completion.SetException(e);
                }
            };

#pragma warning disable 1998
            _queryMessageHandler = async messageParams =>
            {
                try
                {
                    if (messageParams.Message.IsError)
                    {
                        context.Fail(command, message: messageParams.Message.Message);
                        completion.SetResult(true);
                    }
                    else
                    {
                        context.Display(messageParams.Message.Message);
                    }
                }
                catch (Exception e)
                {
                    completion.SetException(e);
                }
            };
#pragma warning restore 1998

            try
            {
                await _serviceClient.ExecuteQueryStringAsync(_tempFileUri, command.Code);

                context.CancellationToken.Register(() =>
                {

                    _serviceClient.CancelQueryExecutionAsync(_tempFileUri)
                        .Wait(TimeSpan.FromSeconds(10));

                    completion.TrySetCanceled(context.CancellationToken);
                });
                await completion.Task;
            }
            catch (TaskCanceledException)
            {
                context.Display("Query cancelled.");
            }
            catch (OperationCanceledException)
            {
                context.Display("Query cancelled.");
            }
            finally
            {
                _queryCompletionHandler = null;
                _queryMessageHandler = null;
            }
        }

        private IEnumerable<IEnumerable<IEnumerable<(string name, object value)>>> GetEnumerableTables(ColumnInfo[] columnInfos, CellValue[][] rows)
        {
            var displayTable = new List<(string, object)[]>();
            var columnNames = columnInfos.Select(info => info.ColumnName).ToArray();

            SqlKernelUtils.AliasDuplicateColumnNames(columnNames);

            foreach (CellValue[] row in rows)
            {
                var displayRow = new (string, object)[row.Length];

                for (var colIndex = 0; colIndex < row.Length; colIndex++)
                {
                    object convertedValue = default;

                    try
                    {
                        var columnInfo = columnInfos[colIndex];

                        var expectedType = MapFromKustoType(columnInfo.DataType);

                        if (TypeDescriptor.GetConverter(expectedType) is { } typeConverter)
                        {
                            if (typeConverter.CanConvertFrom(typeof(string)))
                            {
                                // TODO:fix handling target boolean type when the column is bit type with numeric value
                                if ((expectedType == typeof(bool) || expectedType == typeof(bool?)) &&

                                    decimal.TryParse(row[colIndex].DisplayValue, out var numericValue))
                                {
                                    convertedValue = numericValue != 0;
                                }
                                else
                                {
                                    convertedValue =
                                        typeConverter.ConvertFromInvariantString(row[colIndex].DisplayValue);
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        convertedValue = row[colIndex].DisplayValue;
                    }

                    displayRow[colIndex] = (columnNames[colIndex], convertedValue);
                }

                displayTable.Add(displayRow);
            }

            yield return displayTable;
        }
        
        /// <summary>
        /// Map Kusto type to .NET Type equivalent using scalar data types
        /// </summary>
        /// <seealso href="https://docs.microsoft.com/en-us/azure/data-explorer/kusto/query/scalar-data-types/">Here</seealso>
        /// <param name="type">Kusto Type</param>
        /// <returns>.NET Equivalent Type</returns>
        private static Type MapFromKustoType(string type)
        {
            switch (type)
            {
                case "bool": return Type.GetType("System.Boolean");
                case "datetime": return Type.GetType("System.DateTime");
                case "dynamic": return Type.GetType("System.Object");
                case "guid": return Type.GetType("System.Guid");
                case "int": return Type.GetType("System.Int32");
                case "long": return Type.GetType("System.Int64");
                case "real": return Type.GetType("System.Double");
                case "string": return Type.GetType("System.String");
                case "timespan": return Type.GetType("System.TimeSpan");
                case "decimal": return Type.GetType("System.Data.SqlTypes.SqlDecimal");
                
                default: return typeof(string);
            }
        }

        public async Task HandleAsync(RequestCompletions command, KernelInvocationContext context)
        {
            if (!_intellisenseReady)
            {
                return;
            }

            var completionItems = await _serviceClient.ProvideCompletionItemsAsync(_tempFileUri, command);
            context.Publish(new CompletionsProduced(completionItems, command));
        }

        protected override ChooseKernelDirective CreateChooseKernelDirective() =>
            new ChooseMsKustoKernelDirective(this);

        private class ChooseMsKustoKernelDirective : ChooseKernelDirective
        {
            public ChooseMsKustoKernelDirective(Kernel kernel) : base(kernel, $"Run a Kusto query using the \"{kernel.Name}\" connection.")
            {
                Add(MimeTypeOption);
            }

            private Option<string> MimeTypeOption { get; } = new(
                "--mime-type",
                description: "Specify the MIME type to use for the data.",
                getDefaultValue: () => HtmlFormatter.MimeType);

            protected override async Task Handle(KernelInvocationContext kernelInvocationContext, InvocationContext commandLineInvocationContext)
            {
                await base.Handle(kernelInvocationContext, commandLineInvocationContext);

                switch (kernelInvocationContext.Command)
                {
                    case SubmitCode c:
                        var mimeType = commandLineInvocationContext.ParseResult.FindResultFor(MimeTypeOption)?.GetValueOrDefault();

                        c.Properties.Add("mime-type", mimeType);
                        break;
                }
            }
        }
    }
}