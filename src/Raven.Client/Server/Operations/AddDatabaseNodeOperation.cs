﻿using System;
using System.Net.Http;
using Raven.Client.Documents.Conventions;
using Raven.Client.Http;
using Raven.Client.Json.Converters;
using Sparrow.Json;

namespace Raven.Client.Server.Operations
{
    public class AddDatabaseNodeOperation : IServerOperation<DatabasePutResult>
    {
        private readonly string _databaseName;
        private readonly string _node;

        public AddDatabaseNodeOperation(string databaseName, string node = null)
        {
            MultiDatabase.AssertValidName(databaseName);
            _databaseName = databaseName;
            _node = node;
        }

        public RavenCommand<DatabasePutResult> GetCommand(DocumentConventions conventions, JsonOperationContext context)
        {
            return new AddDatabaseNodeCommand(_databaseName, _node);
        }

        private class AddDatabaseNodeCommand : RavenCommand<DatabasePutResult>
        {
            private readonly string _databaseName;
            private readonly string _node;

            public AddDatabaseNodeCommand(string databaseName, string node)
            {
                if (string.IsNullOrEmpty(databaseName))
                    throw new ArgumentNullException(databaseName);

                _databaseName = databaseName;
                _node = node;
            }

            public override HttpRequestMessage CreateRequest(ServerNode node, out string url)
            {
                url = $"{node.Url}/admin/databases/node?name={_databaseName}";
                if (string.IsNullOrEmpty(_node) == false)
                {
                    url += $"node={node}";
                }

                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Put,
                };

                return request;
            }

            public override void SetResponse(BlittableJsonReaderObject response, bool fromCache)
            {
                if (response == null)
                    ThrowInvalidResponse();

                Result = JsonDeserializationClient.DatabasePutResult(response);
            }

            public override bool IsReadRequest => false;
        }
    }
}