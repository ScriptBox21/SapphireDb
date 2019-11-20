﻿using RealtimeDatabase.Models.Commands;
using RealtimeDatabase.Models.Responses;
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using RealtimeDatabase.Connection;
using RealtimeDatabase.Helper;
using RealtimeDatabase.Models;

namespace RealtimeDatabase.Internal.CommandHandler
{
    class SubscribeRolesCommandHandler : AuthCommandHandlerBase, ICommandHandler<SubscribeRolesCommand>, INeedsConnection
    {
        public ConnectionBase Connection { get; set; }

        public SubscribeRolesCommandHandler(AuthDbContextAccesor authDbContextAccessor, IServiceProvider serviceProvider)
            : base(authDbContextAccessor, serviceProvider)
        {
        }

        public async Task<ResponseBase> Handle(HttpInformation context, SubscribeRolesCommand command)
        {
            await Connection.AddRolesSubscription(command);

            return new SubscribeRolesResponse()
            {
                ReferenceId = command.ReferenceId,
                Roles = ModelHelper.GetRoles(GetContext()).ToList()
            };
        }
    }
}
