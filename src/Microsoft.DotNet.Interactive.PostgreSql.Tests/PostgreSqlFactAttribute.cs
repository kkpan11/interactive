﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using Npgsql;
using Xunit;

namespace Microsoft.DotNet.Interactive.PostgreSql.Tests;

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    private const string TEST_POSTGRESQL_CONNECTION_STRING = nameof(TEST_POSTGRESQL_CONNECTION_STRING);
    private static readonly string _skipReason;

    static PostgreSqlFactAttribute()
    {
        _skipReason = TestConnectionAndReturnSkipReason();
    }

    public PostgreSqlFactAttribute()
    {
        if (_skipReason is not null)
        {
            Skip = _skipReason;
        }
    }

    internal static string TestConnectionAndReturnSkipReason()
    {
        string connectionString = GetConnectionStringForTests();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return
                $"""
                 Environment variable {TEST_POSTGRESQL_CONNECTION_STRING} is not set. To run tests that require PostgreSQL, this environment variable must be set to a valid connection string value.
                 """;
        }

        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
        }
        catch (Exception e)
        {
            return
                $"""
                 A connection could not be established to a PostgreSQL server. Verify the connection string value used for environment variable {TEST_POSTGRESQL_CONNECTION_STRING} targets a running PostgreSQL instance. Connection failed failed with error: {e}
                 """;
        }

        return null;
    }

    public static string GetConnectionStringForTests()
    {
        const string fallbackConnectionString = "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=northwind";

        return Environment.GetEnvironmentVariable(TEST_POSTGRESQL_CONNECTION_STRING) ?? fallbackConnectionString;
    }
}