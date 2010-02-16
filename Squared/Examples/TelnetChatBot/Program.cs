﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Sockets;
using Squared.Task;
using Squared.Task.IO;
using System.IO;
using System.Diagnostics;
using System.Threading;

namespace TelnetChatBot {
    public class DisconnectedException : Exception {
    }

    static class Program {
        static AsyncTextReader Reader;
        static AsyncTextWriter Writer;
        static TaskScheduler Scheduler = new TaskScheduler();
        static bool Disconnected = false;
        static float SendRate;

        static IEnumerator<object> SendTask (SocketDataAdapter adapter) {
            var output = new AsyncTextWriter(adapter, Encoding.ASCII);
            output.AutoFlush = true;
            Writer = output;
            string nextMessageText = String.Format("ChatBot{0:00000}", Process.GetCurrentProcess().Id);
            Console.Title = nextMessageText;
            int i = 0;
            yield return new Sleep(new Random(Process.GetCurrentProcess().Id).NextDouble());
            while (true) {
                var f = output.WriteLine(nextMessageText);
                yield return f;
                if (f.CheckForFailure(typeof(DisconnectedException), typeof(IOException), typeof(SocketException))) {
                    Disconnected = true;
                    throw new DisconnectedException();
                }

                i += 1;

                if ((i % 1000) == 0)
                    Console.WriteLine("Sent: {0}", i);

                nextMessageText = String.Format("Message {0}", i);
                yield return new Sleep(SendRate);
            }
        }

        static IEnumerator<object> ReceiveTask (SocketDataAdapter adapter) {
            var input = new AsyncTextReader(adapter, Encoding.ASCII);
            int i = 0;
            Reader = input;
            while (true) {
                IFuture f = input.ReadLine();
                yield return f;
                if (f.CheckForFailure(typeof(DisconnectedException), typeof(IOException), typeof(SocketException))) {
                    Disconnected = true;
                    throw new DisconnectedException();
                }

                try {
                    string message = (string)f.Result;
                    i += 1;
                } catch (Exception ex) {
                    Console.WriteLine(ex.ToString());
                }

                if ((i % 1000) == 0)
                    Console.WriteLine("Recieved: {0}", i);
            }
        }

        static void Main (string[] args) {
            if ((args.Length < 1) || !float.TryParse(args[0], out SendRate))
                SendRate = 1.0f;

            Thread.CurrentThread.Name = "MainThread";
            ThreadPool.SetMinThreads(1, 1);

            try {
                Console.WriteLine("Connecting to server...");
                var f = Network.ConnectTo("hildr.luminance.org", 1234);
                f.GetCompletionEvent().WaitOne();
                Console.WriteLine("Connected.");
                TcpClient client = f.Result as TcpClient;
                client.Client.Blocking = false;
                client.Client.NoDelay = true;
                SocketDataAdapter adapter = new SocketDataAdapter(client.Client);
                Scheduler.Start(ReceiveTask(adapter), TaskExecutionPolicy.RunAsBackgroundTask);
                Scheduler.Start(SendTask(adapter), TaskExecutionPolicy.RunAsBackgroundTask);

                while (!Disconnected) {
                    Scheduler.Step();
                    Scheduler.WaitForWorkItems();
                }
                Console.WriteLine("Disconnected.");
            } catch (Exception ex) {
                if (ex is TaskException && ex.InnerException is DisconnectedException) {
                } else {
                    Console.WriteLine(ex.ToString());
                }
            }
        }
    }
}
