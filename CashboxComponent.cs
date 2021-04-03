using AirportLibrary;
using AirportLibrary.Delay;
using AirportLibrary.DTO;
using RabbitMqWrapper;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CashboxComponent
{
    class CashboxComponent
    {
        RabbitMqClient mqClient = new RabbitMqClient();

        const string AvailableFlight = Component.InfoPanel + Component.Cashbox;
        const string TicketRequest = Component.Passenger + Component.Cashbox;
        const string Ticket = Component.Passenger + Component.Cashbox;
        const string RefundRequest = Component.Passenger + Component.Cashbox;
        const string RefundResponse = Component.Passenger + Component.Cashbox;
        //const string CashboxToRegistrationQueue = Component.Cashbox + Component.Registration;
        //const string CashboxToLogsQueue = Component.Cashbox + Component.Logs;

        ConcurrentQueue<TicketRequest> ticketRequests = new ConcurrentQueue<TicketRequest>();

        Dictionary<string, FlightStatusUpdate> flights = new Dictionary<string, FlightStatusUpdate>();
        Dictionary<string, string> passengerToFlight = new Dictionary<string, string>();

        const int TICKET_REQUEST_HANDLING_TIME_MS = 20 * 1000;
        const double TIME_FACTOR = 1.0;
        PlayDelaySource delaySource = new PlayDelaySource(TIME_FACTOR);

        AutoResetEvent resetEvent = new AutoResetEvent(false);

        List<string> queues = new List<string>()
        {
            AvailableFlight,
            TicketRequest,
            Ticket,
            RefundRequest,
            RefundResponse
        };

        public void Start()
        {
            mqClient.DeclareQueues(queues.ToArray());
            mqClient.PurgeQueues(queues.ToArray());

            Task.Run(() => {
                try { 
                    while (true)
                    {
                        resetEvent.WaitOne();

                        while (ticketRequests.TryDequeue(out var ticketRequest))
                        {
                            HandleTicketRequest(ticketRequest);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }

            });

            mqClient.SubscribeTo<FlightStatusUpdate>(AvailableFlight, mes =>
            {
                try
                {
                    lock (flights)
                    {
                        if (flights.ContainsKey(mes.FlightId))
                        {
                            flights[mes.FlightId].Status = mes.Status;
                        }
                        else
                        {
                            flights.Add(mes.FlightId, mes);
                        }
                    }
                } catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            });

            mqClient.SubscribeTo<TicketRequest>(TicketRequest, mes =>
            {
                try
                {
                    ticketRequests.Enqueue(mes);
                    resetEvent.Set();
                } catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            });

            mqClient.SubscribeTo<CheckTicketRequest>(RegistrationToCashboxQueue, mes =>
            {
                try
                {
                    HandleCheckTicketRequest(mes);
                } catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            });

            mqClient.SubscribeTo<NewTimeSpeedFactor>(TimeServiceToCashboxQueue, mes =>
            {
                delaySource.TimeFactor = mes.Factor;
            });
        }

        public void HandleTicketRequest(TicketRequest request)
        {
            Console.WriteLine($"Started handling request from {request.PassengerId}...");
            delaySource.CreateToken().Sleep(TICKET_REQUEST_HANDLING_TIME_MS);

            var passId = request.PassengerId;
            var flightId = request.FlightId;
            TicketStatus status;

            try {
                lock (passengerToFlight)
                {
                    lock (flights)
                    {
                        if (!flights.ContainsKey(request.FlightId))
                        {
                            Console.WriteLine($"There is no such flight {flightId} for passenger {passId}");
                            status =
                                request.Action == TicketAction.Buy ? TicketStatus.Late :
                                request.Action == TicketAction.Return ? TicketStatus.LateReturn :
                                (TicketStatus)420;
                        }
                        else
                        {
                            switch (request.Action)
                            {
                                case TicketAction.Buy:
                                    if (passengerToFlight.ContainsKey(passId))
                                    {
                                        status = TicketStatus.AlreadyHasTicket;
                                    }
                                    else
                                    {
                                        var lastUpdate = flights[flightId];
                                        if (lastUpdate.Status == FlightStatus.New
                                            || lastUpdate.Status == FlightStatus.CheckIn)
                                        {
                                            if (lastUpdate.TicketCount > 0)
                                            {
                                                lastUpdate.TicketCount--;
                                                Console.WriteLine($"Tickets left for flight {lastUpdate.FlightId}: {lastUpdate.TicketCount}");
                                                passengerToFlight.Add(passId, flightId);
                                                status = TicketStatus.HasTicket;
                                            } else
                                            {
                                                status = TicketStatus.NoTicketsLeft;
                                            }
                                        }
                                        else
                                        {
                                            status = TicketStatus.Late;
                                        }
                                    }
                                    break;
                                case TicketAction.Return:
                                    if (passengerToFlight.TryGetValue(passId, out var passFlightId)
                                        && passFlightId == flightId)
                                    {
                                        var lastUpdate = flights[flightId];
                                        if (lastUpdate.Status == FlightStatus.New
                                            || lastUpdate.Status == FlightStatus.CheckIn)
                                        {
                                            passengerToFlight.Remove(passId);
                                            flights[flightId].TicketCount++;
                                            status = TicketStatus.TicketReturn;
                                        } else
                                        {
                                            status = TicketStatus.LateReturn;
                                        }
                                    } else
                                    {
                                        status = TicketStatus.ReturnError;
                                    }
                                    break;
                                default:
                                    var message = $"Unknown action of {nameof(request)}.Action: {request.Action}";
                                    Console.WriteLine(message);
                                    throw new Exception(message);
                            }
                        }
                    }
                } 
            } catch (Exception e)
            {
                status = (TicketStatus) (-1);
                Console.WriteLine(e);
            }

            Console.WriteLine($"Passenger {passId} gets {status} for flight {flightId}");
            mqClient.Send(
                CashboxToPassengerQueue, 
                new TicketResponse()
                {
                    PassengerId = request.PassengerId,
                    Status = status
                }
            );

        }

        private void HandleCheckTicketRequest(CheckTicketRequest mes)
        {
            try
            {
                lock (passengerToFlight)
                {
                    var hasTicket = passengerToFlight.TryGetValue(mes.PassengerId, out var flightId)
                                    && flightId == mes.FlightId;
                    Console.WriteLine($"Check passenger {mes.PassengerId} ticket for flight {mes.FlightId}: {hasTicket}");
                    mqClient.Send(
                        CashboxToRegistrationQueue,
                        new CheckTicketResponse()
                        {
                            PassengerId = mes.PassengerId,
                            HasTicket = hasTicket
                        }
                    );
                }
            } catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }
}
