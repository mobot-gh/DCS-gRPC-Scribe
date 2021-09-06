﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RurouniJones.DCScribe.Shared.Interfaces;
using RurouniJones.DCScribe.Shared.Models;

namespace RurouniJones.DCScribe.Core
{
    public class Scribe
    {
        /*
         * Configuration for the GameServer including DB and RPC information
         */
        public GameServer GameServer { get; set; }

        /*
         * The RPC client that connects to the server and receives the unit updates
         * to put into the update queue
         */
        private readonly IRpcClient _rpcClient;

        /*
         * The client that handles database actions.
         */
        private readonly IDatabaseClient _databaseClient;

        private readonly ILogger<Scribe> _logger;
        public Scribe(ILogger<Scribe> logger, IRpcClient rpcClient, IDatabaseClient databaseClient)
        {
            _logger = logger;
            _rpcClient = rpcClient;
            _databaseClient = databaseClient;
        }

        public async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _rpcClient.HostName = GameServer.Rpc.Host;
            _rpcClient.Port = GameServer.Rpc.Port;

            _databaseClient.Host = GameServer.Database.Host;
            _databaseClient.Port = GameServer.Database.Port;
            _databaseClient.Name = GameServer.Database.Name;
            _databaseClient.Username = GameServer.Database.Username;
            _databaseClient.Password = GameServer.Database.Password;

            while (!stoppingToken.IsCancellationRequested)
            {
                var scribeTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var scribeToken = scribeTokenSource.Token;
                
                // Clear the database as we start from scratch each time around
                await _databaseClient.ClearTableAsync();

                /*
                 * A queue containing all the unit updates to be processed. We populate
                 * this queue in a separate thread to make sure that slowdowns in unit
                 * processing do not impact the rate at which we can receive unit updates
                 *
                 * We clear the queue each time we connect
                 */
                var queue = new ConcurrentQueue<Unit>();
                _rpcClient.UpdateQueue = queue;

                var tasks = new List<Task>
                {
                    _rpcClient.ExecuteAsync(scribeToken), // Get the events and put them into the queue
                    ProcessQueue(queue, scribeToken), // Process the queue events into the units dictionary
                };

                await Task.WhenAny(tasks); // If one task finishes (usually when the RPC client gets disconnected on
                                           // mission restart
                scribeTokenSource.Cancel(); // Then cancel all of the other tasks
                await Task.WhenAll(tasks); // Then we wait for all of them to finish before starting the loop again
            }
        }

        private async Task ProcessQueue(ConcurrentQueue<Unit> queue, CancellationToken scribeToken)
        {
            var unitsToUpdate = new ConcurrentDictionary<uint, Unit>();
            var unitsToDelete = new List<uint>();
            var startTime = DateTime.UtcNow;

            while (!scribeToken.IsCancellationRequested)
            {
                queue.TryDequeue(out var unit);
                if (unit == null)
                {
                    await Task.Delay(5, scribeToken);
                    continue;
                }

                if (unit.Deleted)
                {
                    unitsToDelete.Add(unit.Id);
                }
                else
                {
                    unitsToUpdate[unit.Id] = unit;
                }

                if (!((DateTime.UtcNow - startTime).TotalMilliseconds > 2000)) continue;
                // Every X seconds we will write the accumulated data to the database
                try
                {
                    if (unitsToUpdate.Count > 0)
                    {
                        var updates = new Unit[unitsToUpdate.Count];
                        unitsToUpdate.Values.CopyTo(updates, 0);
                        await UpdateUnitsAsync(updates.ToList(), scribeToken);
                    }

                    if (unitsToDelete.Count > 0)
                    {
                        var deletions = new uint[unitsToDelete.Count];
                        unitsToDelete.CopyTo(deletions, 0);
                        await DeleteUnitsAsync(deletions.ToList(), scribeToken);
                    }
                    // Then clear the updates and start again
                    unitsToUpdate = new ConcurrentDictionary<uint, Unit>();
                    unitsToDelete = new List<uint>();
                    startTime = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing queue");
                }
            }
        }

        private async Task UpdateUnitsAsync(List<Unit> units, CancellationToken scribeToken)
        {
            _logger.LogInformation("Writing\t {count} \t unit(s) to database at\t\t {time}",units.Count, DateTimeOffset.Now);
            await _databaseClient.UpdateUnitsAsync(units, scribeToken);
        }

        private async Task DeleteUnitsAsync(List<uint> ids, CancellationToken scribeToken)
        {
            _logger.LogInformation("Deleting\t {count} \t unit(s) from database at\t {time}",ids.Count, DateTimeOffset.Now);
            await _databaseClient.DeleteUnitsAsync(ids, scribeToken);
        }
    }
}
