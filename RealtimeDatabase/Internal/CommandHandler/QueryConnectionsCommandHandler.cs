﻿using RealtimeDatabase.Models.Commands;
using RealtimeDatabase.Models.Responses;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using RealtimeDatabase.Connection;
using RealtimeDatabase.Helper;
using RealtimeDatabase.Models;

namespace RealtimeDatabase.Internal.CommandHandler
{
    class QueryConnectionsCommandHandler : CommandHandlerBase, ICommandHandler<QueryConnectionsCommand>
    {
        private readonly RealtimeConnectionManager connectionManager;

        public QueryConnectionsCommandHandler(DbContextAccesor dbContextAccessor, RealtimeConnectionManager connectionManager)
            : base(dbContextAccessor)
        {
            this.connectionManager = connectionManager;
        }

        public Task<ResponseBase> Handle(HttpInformation context, QueryConnectionsCommand command)
        {
            if (context.User.Identity.IsAuthenticated)
            {
                string userId = context.User.Claims.FirstOrDefault(c => c.Type == "Id")?.Value;

                if (!string.IsNullOrEmpty(userId))
                {
                    QueryConnectionsResponse response = new QueryConnectionsResponse()
                    {
                        Connections = connectionManager.connections.Where(c => c.Information.UserId == userId).ToList(),
                        ReferenceId = command.ReferenceId
                    };

                    return Task.FromResult<ResponseBase>(response);
                }
            }

            return Task.FromResult(command.CreateExceptionResponse<QueryConnectionsResponse>("Not authenticated"));
        }
    }
}
