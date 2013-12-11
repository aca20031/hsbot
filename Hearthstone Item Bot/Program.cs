﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HSBot
{
    class Program
    {
        static void Main(string[] args)
        {
            
            
            
            
            Config.Reload();
            
            Object hsInstall = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Hearthstone","InstallLocation",null);

            String cardData = null;

            if (hsInstall != null)
            {
                cardData = System.IO.Path.Combine((String)hsInstall, "Data", "Win", "cardxml0.unity3d");
            }
            if (cardData == null || !System.IO.File.Exists(cardData))
            {
                Console.WriteLine("Hearthstone installation not found. Enter the path to cardxml0.unity3d or enter nothing to continue without extracting new card data.");
                String input = Console.ReadLine();
                if (!String.IsNullOrEmpty(input) && System.IO.File.Exists(input))
                    cardData = input;
                else
                    cardData = null;
            }
            if (cardData != null && System.IO.File.Exists(cardData))
                Cards.FileExtractor.Extract(cardData,Config.DataDirectory);

            IRC irc;
            DateTime lastMessage = new DateTime(1);
            // A hacked together reconnect check loop to fit a hacked together IRC library.
            new System.Threading.Thread(
                    new System.Threading.ThreadStart(
                        () => {
                            while (true)
                            {
                                if ((DateTime.Now - lastMessage) > TimeSpan.FromMinutes(2))
                                {
                                    Console.WriteLine("Not connected...attempting to connect...");
                                    lastMessage = DateTime.Now;
                                    irc = new IRC();
             
                                    irc.RawMessageReceived += new EventHandler<IrcDotNet.IrcRawMessageEventArgs>((arguments, sender) =>
                                    {
                                     //   Console.WriteLine("Debug: {0}", ((IrcDotNet.IrcRawMessageEventArgs)arguments).RawContent);
                                        lastMessage = DateTime.Now;
                                    });
                                    irc.StartConnect();
                                }
                                System.Threading.Thread.Sleep(2 * (60 * 1000));
                                

                            }
                        
                        }
                        )
                
                ).Start();

           
        }
    }
}
