﻿/**
 * 
 * Ben Buzbee's Lightwight, Reliable and Tiny IRC Client class
 * LRTIRC aims to be an IRC client that simply works.  There are many IRC libraries out there,
 * most of them require complicated setup and linking and after using one you almost always find
 * a blocking bug.  LRTIRC takes a "keep it simple stupid" approach to ensure good, quality, working IRC.
 * 
 * Some general design principals:
 * 1.) Use as few classes as necessary
 * 2.) Implement the IRC RFC to spec where possible
 * 3.) Support server-specific compaibility without compromising #2
 * 4.) Require as little programmer input as possible to function as expected
 * 5.) Use smart multithreading techniques
 * And most importantly, WORK. Always.
 * 
 * Tips for reading the code:
 * To ensure proper order-of-operation, all internal bookkeeping for an event is handled first in an
 * internal event (functions which begin with "ie*") before calling external event handlers as their last step.
 * 
 * Classes are used as data respresentation only to simplify complex relationships. They are not meant to allow
 * specific function calls - e.g. no ChannelUser.Message! They are also used very sparingly by design.
 * 
 * If this class doesn't work with an IRC server or obvious scenario, let me know! Ben@St0rm.Net
 **/
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;

namespace benbuzbee.LRTIRC
{
    /// <summary>
    /// A Client on an IRC server
    /// </summary>
    public class IrcClient : IDisposable
    {
        #region Public Properties
        /// <summary>
        /// Gets the last exception thrown
        /// </summary>
        public Exception Exception { private set; get; }
        /// <summary>
        /// Gets the underlying TcpClient.  You usually don't want to mess with this.
        /// </summary>
        public TcpClient TCP { private set; get; }
        /// <summary>
        /// Gets the nickname used the last time Connect was called
        /// </summary>
        public String Nick { private set; get; }
        /// <summary>
        /// Gets the username used the last time Connect was called
        /// </summary>
        public String Username { private set; get; }
        /// <summary>
        /// Gets the real name used the last time Connect was called
        /// </summary>
        public String RealName { private set; get; }
        /// <summary>
        /// Gets the host used the last time Connect was called
        /// </summary>
        public String Host { private set; get; }
        /// <summary>
        /// Gets the port used the last time Connect was called
        /// </summary>
        public int Port { private set; get; }
        /// <summary>
        /// Gets the password used the last time Connect was called
        /// </summary>
        public String Password { private set; get; }
        /// <summary>
        /// The last time a message was received from the server
        /// </summary>
        public DateTime LastMessageTime { private set; get; }
        /// <summary>
        /// Set to false until TcpClient connects. Does not necessarily mean we are registered. Set before OnConnect event.
        /// </summary>
        public bool Connected { private set; get; }
        /// <summary>
        /// Gets or sets how long without a message before OnTimeout is raised.  Changes will only take affect on the connect proceeding the change.
        /// </summary>
        public TimeSpan Timeout { set; get; }
        /// <summary>
        /// Sets the Encoding. Defaults to UTF8 without BOM. Will take affect next connect.
        /// </summary>
        public Encoding Encoding { set; get; }
        /// <summary>
        /// Set to false before PASS/NICK/USER strings are sent, the set to TRUE (does not wait on confirmation from server)
        /// </summary>
        public Boolean Registered { private set; get; }
        /// <summary>
        /// Channels which this client is currently in
        /// </summary>
        public IEnumerable<Channel> Channels { get { return m_channels.Values; } }
        /// <summary>
        /// Information about the server sent on connection
        /// </summary>
        public ServerInfoType ServerInfo { get; private set; }
        /// <summary>
        /// By default, events are raised on a background thread as a Task which means order is not guaranteed.
        /// Set this to true to force events to be raised directly on the Irc Reader thread
        /// </summary>
        public bool SingleThreadedEvents { get; set; }
        #endregion Public Properties

        #region Private Members
        /// <summary>
        /// Lock when registering the client with the server so nothing interferes
        /// </summary>
        private Object m_registrationMutex = new Object();
        /// <summary>
        /// Used when writing so there are not concurrent attempts to jumble our messages
        /// </summary>
        private SemaphoreSlim m_writingSemaphore = new SemaphoreSlim(1, 1);

        /// <summary>
        /// These are channels which this user is in.  It is a map of lower(channel name) -> Channel Object for easy lookup
        /// </summary>
        private IDictionary<String, Channel> m_channels = new ConcurrentDictionary<String, Channel>();
        
        private StreamWriter m_streamWriter;

        /// <summary>
        /// Detects a timeout if it elapses and too much time has past since the last message
        /// </summary>
        private Timer m_timeoutTimer;
        /// <summary>
        /// Proactively sends client PING requests. Some servers do not ping us if we're being active because they know we are still alive
        /// We need a way to be sure they are too!
        /// </summary>
        private Timer m_pingTimer;
        /// <summary>
        /// A map of: lower(Channel) -> (Nick -> Prefix List)
        /// </summary>
        private ConcurrentDictionary<string, ConcurrentDictionary<string, StringBuilder>> m_channelPrefixMap = new ConcurrentDictionary<string, ConcurrentDictionary<string, StringBuilder>>();
        /// <summary>
        /// Thread that reads the socket's input stream. Initialized in the constructor and woken up on connect
        /// </summary>
        private IrcReader m_readingThread;
        /// <summary>
        /// Ordered list of filters applied to outgoing messages before they are sent
        /// </summary>
        private List<IIrcMessageFilter> m_filtersOutgoing = new List<IIrcMessageFilter>();
        /// <summary>
        /// Ordered list of filters applied to incoming messages before they are processed
        /// </summary>
        private List<IIrcMessageFilter> m_filtersIncoming = new List<IIrcMessageFilter>();
        #endregion Private Members

        /// <summary>
        /// Structure containing general information about the server - only that which is needed by the IrcClient for further action.
        /// For specific info, you should capture it yourself from the appropriate event
        /// </summary>
        public class ServerInfoType
        {
            private String m_PREFIX;
            /// <summary>
            /// The PREFIX sent in numeric 005. Null until then.
            /// </summary>
            public String PREFIX { internal set { m_PREFIX = value; PREFIX_modes = value.Substring(1, value.IndexOf(')') - 1); PREFIX_symbols = value.Substring(value.IndexOf(')') + 1); } get { return m_PREFIX; } }
            /// <summary>
            /// The modes portion of PREFIX (such as 'o' or 'v'). Null until PREFIX is set.
            /// </summary>
            public String PREFIX_modes;
            /// <summary>
            /// The Symbols portion of PREFIX (such as '@' or '+'); Null until PREFIX is set.
            /// </summary>
            public String PREFIX_symbols;

            private String _CHANMODES;
            /// <summary>
            /// The CHANMODES parameter sent in numeric 005. Null until then.
            /// </summary>
            public String CHANMODES { get { return _CHANMODES; } internal set { _CHANMODES = value; String[] groups = value.Split(','); CHANMODES_list = groups[0]; CHANMODES_parameterAlways = groups[1]; CHANMODES_paramaterToSet = groups[2]; CHANMODES_parameterNever = groups[3]; } }
            /// <summary>
            /// The first group in CHANMODES. These are channel modes that modify a list (bans, invites, etc)
            /// </summary>
            public String CHANMODES_list;
            /// <summary>
            /// The second group in CHANMODES. These are modes that always have a parameter
            /// </summary>
            public String CHANMODES_parameterAlways;
            /// <summary>
            /// The third group in CHANMODES. These are modes that have a parameter when being set, but not unset.
            /// </summary>
            public String CHANMODES_paramaterToSet;
            /// <summary>
            /// The fourth group in CHANMOEDS. These are modes that never have a parameter.
            /// </summary>
            public String CHANMODES_parameterNever;
        }

        // This region contains event handlers for events.  The last step of which is usually to signal all external event handlers
        #region Internal Events
        /// <summary>
        /// The main event.  Most IRC events originate here - it is called when any newline delimited string is received from the server
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="message"></param>
        private void ieOnMessageReceived(IrcClient sender, String message)
        {
            // Apply all incoming filters in order
            lock (m_filtersIncoming)
            {
                foreach (var filter in m_filtersIncoming)
                {
                    try
                    {
                        message = filter.FilterMessage(sender, message);
                    } catch (Exception e)
                    {
                        // Ok to ignore filter-side errors in release mode to increase robustness.  
                        RaiseEvent(OnException, sender, e);
                        Debug.Assert(false, "Incoming message filter threw exception", e.Message);
                    }
                }
            }

            String[] tokens = message.Split(' ');
            LastMessageTime = DateTime.Now;

            // Scans message for errors and raises error events if it finds one
            ErrorHandler(sender, message);

            // Takes action if the message is a PING
            PingHandler(sender, message);

            // Takes action if the message is a numeric
            NumericHandler(sender, message);

            // Takes action if the message is a PRIVMSG
            PrivmsgHandler(sender, message);

            // Takes action if the message is a JOIN
            if (tokens.Length >= 3 && tokens[1].Equals("JOIN"))
            {
                ieOnJoin(sender, tokens[0].Replace(":", ""), tokens[2].Replace(":", ""));   
            }

            // Takes action if the message is a QUIT
            if (tokens.Length >= 3 && tokens[1].Equals("QUIT"))
            {
                ieOnQuit(sender, tokens[0].Replace(":", ""), message.Substring(message.IndexOf(":", 1)));
            }

            // Takes action if the message is a MODE
            if (tokens.Length >= 4 && tokens[1].Equals("MODE"))
            {
                ieOnMode(sender,
                         tokens[0].Replace(":", ""), 
                         tokens[2], 
                         message.Substring(message.IndexOf(tokens[2]) + tokens[2].Length + 1));
            }

            // Takes action if the message is a PART
            if (tokens.Length >= 3 && tokens[1].Equals("PART"))
            {
                String reason = tokens.Length >= 4 ? message.Substring(message.IndexOf(':', 1)) : null;
                ieOnPart(this, tokens[0].Replace(":", ""), tokens[2], reason);
               
            }

            // Takes action if the message is a KICK
            if (tokens.Length >= 5 && tokens[1].Equals("KICK"))
            {
                String source = tokens[0].Replace(":", ""), channel = tokens[2], target = tokens[3];
                String reason = message.Substring(message.IndexOf(':', 1));
                ieOnKick(this, source, target, channel, reason);
            }

            // Takes action if the message is a NICK
            if (tokens.Length >= 3 && tokens[1].Equals("NICK"))
            {
                ieOnNick(sender, tokens[0].Replace(":", ""), tokens[2].Replace(":", ""));
            }

            // Signal external event handlers for raw messages
            // Keeps this at the end of the method so that the more specific event, as well as internal events, are all handled first
            RaiseEvent(OnRawMessageReceived, sender, message);
           
        }

        /// <summary>
        /// Internal event signaled when a client changes his nickname
        /// </summary>
        /// <param name="sender">The IrcClient which received this event</param>
        /// <param name="source">The client changing his nick</param>
        /// <param name="newNick">The new nick for the client</param>
        private void ieOnNick(IrcClient sender, String source, String newNick)
        {

            // Update ChannelUsers in all my chanels
            String oldNick = ChannelUser.GetNickFromFullAddress(source);
            ChannelUser user = null;

            // When we change our nickname
            if (ChannelUser.GetNickFromFullAddress(source) == this.Nick)
            {
                this.Nick = newNick;
            }
            bool fFoundUser = false;
            lock (m_channels)
            {
                foreach (Channel c in m_channels.Values)
                {

                    if (!c.Users.TryGetValue(oldNick.ToLower(), out user))
                    {
                        // If user is not in this channel, check next channel
                        continue;
                    }

                    fFoundUser = true;

                    user.Nick = newNick;
                    c.Users.Remove(oldNick.ToLower());
                    c.Users[newNick.ToLower()] = user;

                }
            }

            Debug.Assert(fFoundUser, "A user changed his nickname but doesn't appear to be in our nick list. Nick change: {0} -> {1}", oldNick, newNick);
            RaiseEvent(OnRfcNick, sender, source, newNick);

        }

        /// <summary>
        /// Internal event signaled when a client is kicked from a channel.  The last thing it does is raise external event handlers
        /// </summary>
        /// <param name="sender">The IrcClient that received this message</param>
        /// <param name="source">The source of the event (probably a channel op)</param>
        /// <param name="target">The client being kicked</param>
        /// <param name="channel">The channel from which the client is being kicked</param>
        /// <param name="reason">The message given during the kick</param>
        private void ieOnKick(IrcClient sender, String source, String target, String channel, String reason)
        {
            Channel channelObject = null;
            lock (m_channels)
            {
                m_channels.TryGetValue(channel.ToLower(), out channelObject);
                Debug.Assert(channelObject != null, "Any channel on which we receive a KICK should be in our channel list", "Channel: {0}", channel);

                if (Nick.Equals(target, StringComparison.CurrentCultureIgnoreCase)) // If it's us, remove the channel entirely
                {    
                    m_channels.Remove(channel.ToLower());   
                }
                else // else remove the nick from the channel 
                {
                    try
                    {
                        channelObject.Users.Remove(target.ToLower());
                    }
                    catch (Exception)
                    {
                        Debug.Assert(false, "Unknown user kicked. User: {0}", target); // If the user isn't there...good! But why?
                    } 
                }
            }

            RaiseEvent(OnRfcKick, this, source, target, channel, reason);
            
        }
        /// <summary>
        /// Internal event called when a client parts a channel
        /// </summary>
        /// <param name="sender">The IrcClient which received this event</param>
        /// <param name="source">The client parting</param>
        /// <param name="target">The channel being parted</param>
        /// <param name="message">Optional message for parting</param>
        private void ieOnPart(IrcClient sender, String source, String target, String message)
        {

            // Remove user from nicklist
            lock (m_channels)
            {
                Channel channelObject = null;

                if (m_channels.TryGetValue(target.ToLower(), out channelObject))
                {
                    if (ChannelUser.GetNickFromFullAddress(source).Equals(Nick, StringComparison.CurrentCultureIgnoreCase))
                    {
                        m_channels.Remove(target.ToLower());
                    }
                    else
                    {
                        String nick = ChannelUser.GetNickFromFullAddress(source);
                        try
                        {
                            channelObject.Users.Remove(nick.ToLower());
                        }
                        catch (Exception) 
                        {
                            Debug.Assert(false, "User parted who wasn't in our list. Nick: {0}", nick); 
                        }
                    }
                }
            }

            RaiseEvent(OnRfcPart, sender, source, target, message);

        }

        /// <summary>
        /// Internal event called when a mode changes.  The last function of this method is to call external handlers
        /// </summary>
        /// <param name="sender">The IrcClient which received this event as a message</param>
        /// <param name="source">The client or server who initiated the mode change</param>
        /// <param name="target">The client/server/channel which is having its mode changed</param>
        /// <param name="modes">String of mode changes</param>
        private void ieOnMode(IrcClient sender, String source, String target, String modes)
        {
            Channel channel = null;
            String[] tokens = modes.Split(' ');

            lock (m_channels)
            {
                if (m_channels.TryGetValue(target.ToLower(), out channel))
                {
                    // Set up some default values
                    if (ServerInfo.CHANMODES == null)
                    {
                        ServerInfo.CHANMODES = "b,k,l,imnpstr";
                    }
                    if (ServerInfo.PREFIX == null)
                    {
                        ServerInfo.PREFIX = "(ohv)@%";
                    }

                    // This loop walks through the mode list and keeps track for each one 1.) The mode, 2.) If it is set 3.) If it has a parameter and 4.) The index of the parameter
                    // It's purpose is to update the ChannelUser for any user that have their modes affected
                    bool isSet = false;
                    for (int modeIndex = 0, parameterIndex = 1; modeIndex < tokens[0].Length; ++modeIndex)
                    {
                        char mode = tokens[0][modeIndex];
                        if (mode == '+')
                        {
                            isSet = true;
                        }
                        else if (mode == '-')
                        {
                            isSet = false;
                        }
                        else if (ServerInfo.CHANMODES_parameterNever.Contains(mode))
                        {
                            continue; // There are no parameters assocaited with this mode, so it can't change a user's prefix
                        }
                        else if (ServerInfo.CHANMODES_paramaterToSet.Contains(mode))
                        {
                            if (!isSet)
                            {
                                continue; // This mode only has a parameter when being set, so it does not have a parameter in the list if it is not being set
                            }
                            else
                            {
                                ++parameterIndex; // This mode consumes one of the parameters
                            }
                        }
                        else  // These modes always associate with a parameter
                        {

                            try
                            {
                                // If it's a user access mode
                                if (ServerInfo.PREFIX_modes.Contains(mode))
                                {
                                    ChannelUser user = null;
                                    String nick = tokens[parameterIndex];
                                    channel.Users.TryGetValue(nick.ToLower(), out user);

                                    // Debug.Assert(user != null, "Mode set on user who was not in our list.", tokens[parameterIndex].ToLower());

                                    if (user == null)
                                    {
                                        // This can happen if users are hidden or servers get a little loose with the rules, so let's pretend like he's in there.
                                        user = new ChannelUser(nick, channel);
                                        channel.Users.Add(nick.ToLower(), user);
                                    }

                                    int modeIndexIntoPrefixList = ServerInfo.PREFIX_modes.IndexOf(mode);
                                    Debug.Assert(modeIndexIntoPrefixList >= 0, "Mode set on user that was not in PREFIX list. This could be because we fell back to a default list.");
                                    char prefix = ServerInfo.PREFIX_symbols[modeIndexIntoPrefixList];
                                    if (isSet)
                                    {
                                        user.InsertPrefix(ServerInfo, prefix);
                                    }
                                    else
                                    {
                                        user.DeletePrefix(prefix);
                                    }
                                }
                            }
                            finally
                            {
                                // These modes always have parameters so we need to always increase the index at the end
                                ++parameterIndex;
                            }
                        }
                    }
                }
            }

            RaiseEvent(OnRfcMode, sender, source, target, modes);
            
        }

        /// <summary>
        /// Internal event called when a user on a common channel quits from the server
        /// </summary>
        /// <param name="sender">The IrcClient which received this event</param>
        /// <param name="source">The client which sent it (the one who quit)</param>
        /// <param name="message">The message given for quitting</param>
        private void ieOnQuit(IrcClient sender, String source, String message)
        {
            //Remove this nick from all channels we are in
            String nick = ChannelUser.GetNickFromFullAddress(source);
            lock (m_channels)
            {
                foreach (Channel c in m_channels.Values)
                {
                    try { c.Users.Remove(nick.ToLower()); }
                    catch (Exception) { Debug.Assert(false, "User quit but he was not in our channel list. Nick: {0}", nick); }

                }
            }

            RaiseEvent(OnRfcQuit, sender, source, message);
            
        }

        /// <summary>
        /// Internal event called when a numeric is received from the server.  It will call more specific numeric handles where they apply before calling the general handler
        /// </summary>
        /// <param name="sender">The IrcClient which received this numeric</param>
        /// <param name="source">Client/server which sent it</param>
        /// <param name="numeric">The numeric</param>
        /// <param name="target"></param>
        /// <param name="other"></param>
        private void ieOnNumericReceived(IrcClient sender, String source, int numeric, String target, String other)
        {

            if (numeric == 1)
            {
                lock (m_channels)
                {
                    m_channels.Clear(); 
                }
                RaiseEvent(OnConnect, sender);
            }

            // Parses numeric 5 (List of things the server supports) and calls event with the parsed list
            else if (numeric == 5)
            {
                // Parse parameters
                Dictionary<String, String> parameters = new Dictionary<string, string>();
                String[] tokens = other.Split(' ');
                foreach (String token in tokens)
                {
                    int equalIndex = token.IndexOf('=');
                    if (equalIndex >= 0)
                    {
                        parameters[token.Substring(0, equalIndex)] = token.Substring(equalIndex + 1);
                    }
                    else
                    {
                        parameters[token] = "";
                    }
                }


                // try to update server info struct for values we care about
                String value;
                if (parameters.TryGetValue("PREFIX", out value))
                {
                    sender.ServerInfo.PREFIX = value;
                }

                if (parameters.TryGetValue("CHANMODES", out value))
                {
                    sender.ServerInfo.CHANMODES = value;
                }

                // If the server supports user-host names, request it
                if (parameters.ContainsKey("UHNAMES"))
                {
                    var task = SendRawMessageAsync("PROTOCTL UHNAMES");
                }

                // If the server supports extended names, request it
                if (parameters.ContainsKey("NAMESX"))
                {
                    var task = SendRawMessageAsync("PROTOCTL NAMESX");
                }

                
                // Signal external events for isupport
                RaiseEvent(OnISupport, this, parameters);
            }
     
            else if (numeric == 353)
            {
                String[] words = other.Split(' ');
                String channel = words[1];
                String names = other.Substring(other.IndexOf(':') + 1).Trim();
                ieOnNames(sender, channel, names);   
            }

            RaiseEvent(OnRfcNumeric, sender, source, numeric, target, other);
         
        }

        /// <summary>
        /// Called when a NAMES message is received.  Does internal bookkeeping before raising external event handlers
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="channel"></param>
        /// <param name="names">Space delimited list of names.  May include prefixes and user-host values if NAMESX and UHNAMES are supported</param>
        private void ieOnNames(IrcClient sender, String channel, String names)
        {

            // Parses names reply to fill ChannelUser list for Channel
            Channel channelObject = null;
            lock (m_channels)
            {
                // Get the channel object or create a new one.  Don't add it to the _channels map if it's new unless we're in it
                if (!m_channels.TryGetValue(channel.ToLower(), out channelObject))
                    channelObject = new Channel(channel);

                String[] namesArray = names.Split(' ');
                foreach (String name in namesArray)
                {
                    Debug.Assert(!String.IsNullOrEmpty(name));
                    
                    // if there are symbols (because NAMESX was supported) find the start of the name, otherwise its position 0
                    int nameStart = 0;
                    for (nameStart = 0; sender.ServerInfo.PREFIX_symbols != null && sender.ServerInfo.PREFIX_symbols.Contains(name[nameStart]); ++nameStart) ;
                    String justName = name.Substring(nameStart);

                    // Create a ChannelUser for this user if it does not exist in the channel, or get it if it does
                    ChannelUser user;
                    if (!channelObject.Users.TryGetValue(ChannelUser.GetNickFromFullAddress(justName), out user))
                    {
                        user = new ChannelUser(justName, channelObject);
                        channelObject.Users[user.Nick.ToLower()] = user;
                    }

                    // Insert each prefix in the names reply (for NAMESX) into the ChannelUser (InsertPrefix ignores duplicates)
                    for (int i = 0; i < nameStart; ++i)
                    {
                        user.InsertPrefix(sender.ServerInfo, name[i]);
                    }

                    /// If we are in the NAMES reply for a channel, that means we are in that channel and should make sure it is in our list
                    if (user.Nick.Equals(Nick, StringComparison.CurrentCultureIgnoreCase))
                    {
                        m_channels[channelObject.Name.ToLower()] = channelObject;
                    }
                }
            }

            RaiseEvent(OnNamesReply, sender, channel, names);
        }

        /// <summary>
        /// Called when JOIN is received from the server (a user joined a channel).  Does internal bookkeeping before raising external events
        /// </summary>
        /// <param name="sender">The IrcClient which received this message</param>
        /// <param name="source">The IRC user who joined</param>
        /// <param name="channellist">Comma delimited list of channels the user has joined</param>
        private void ieOnJoin(IrcClient sender, String source, String channellist)
        {

            // Add the channel to our list if we don't have it
            // Add the user as a ChannelUser
            foreach (String channelName in channellist.Split(','))
            {
                
                lock (m_channels)
                {
                    Channel c = null;    
                    if (!m_channels.TryGetValue(channelName.ToLower(), out c))
                    {
                        c = new Channel(channelName);
                        m_channels[channelName.ToLower()] = c;
                    }
                    
                    ChannelUser u = new ChannelUser(source, c);
                    Debug.Assert(!c.Users.ContainsKey(u.Nick.ToLower()), "Received a JOIN for a user that was already in the ChannelUser list", "User: {0}", u.Nick);
                    c.Users[u.Nick.ToLower()] = u;
                    
                }
            }

            RaiseEvent(OnRfcJoin, sender, source, channellist);
 
        }
        #endregion

        /// <summary>
        /// Represents one connection to one IRC server
        /// </summary>
        public IrcClient()
        {
            ServerInfo = new ServerInfoType();

            Encoding = new System.Text.UTF8Encoding(false);
            Registered = false;
            LastMessageTime = DateTime.Now;
            Timeout = new TimeSpan(0, 2, 0);
            Connected = false;
            SingleThreadedEvents = false;

            m_readingThread = new IrcReader(this);

            // When a message is received on the reader, call internal onMessageReceived
            // This is the real worker that calls the other internal events
            m_readingThread.OnRawMessageReceived += (sender, msg) =>
            {
                ieOnMessageReceived(this, msg);
            };

            // When the reader raises an exception
            m_readingThread.OnException += (sender, e) =>
            {
                Log.Info("Exception detected, exception message is \"{0}\"",e.Message);
                // Call to clean up resources and set flags
                // We cannot recover from a broken reader
                DisconnectInternal();

                // Notify clients
                RaiseEvent(OnException, this, e);
                RaiseEvent(OnDisconnect, this);
            };

            m_timeoutTimer = new Timer(TimeoutTimerElapsedHandler, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            m_pingTimer = new Timer((state) => { var task = SendRawMessageAsync("PING :LRTIRC"); }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }

        ~IrcClient()
        {
            Dispose();
        }

        #region IO
        /// <summary>
        /// Disconnects the client.
        /// </summary>
        public void Disconnect()
        {
            // We don't want callers to deal with the thread synchronization so this is just a wrapper
            // around the internal function. External callers would not have the semaphore
            DisconnectInternal();
        }
        /// <summary>
        /// Disconnects client and disposes of streams, disposes of timeout and ping timer and recreates them in an idle state
        /// </summary>
        /// <param name="callerHasSemaphore">True if we have the m_writingSemaphore before entering</param>
        private void DisconnectInternal(bool callerHasSemaphore = false)
        {
            Log.Info("DisconnectInternal(callerHasSemaphore = {0})",callerHasSemaphore);
            try
            {
                if (!callerHasSemaphore)
                {
                    m_writingSemaphore.Wait();
                }

                lock (m_registrationMutex)
                {
                    lock (m_timeoutTimer)
                    {
                        Log.Info("Timeout timer is suspended by DisconnectInternal()");
                        // Suspend timer
                        m_timeoutTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                    }

                    lock (m_pingTimer)
                    {
                        Log.Info("Ping timer is suspended by DisconnectInternal()");
                        // Suspend timer
                        m_pingTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                    }

                    Connected = false;
                    Registered = false;

                    // Try a semi-graceful kick before closing the stream-behind
                    m_readingThread.ReleaseStream();

                    if (TCP != null && TCP.Connected)
                    {
                        try
                        {
                            TCP.Close();
                            TCP = null;
                        }
                        catch (Exception) { } // Eat exceptions since this is just an attempt to clean up
                    }

                    if (m_streamWriter != null)
                    {
                        try
                        {
                            m_streamWriter.Dispose();
                            m_streamWriter = null;
                        }
                        catch (Exception) { } // Eat exceptions since this is just an attempt to clean up
                    }
                }
            } 
            finally
            {
                // If the caller doesn't have the semaphore, then we do
                if (!callerHasSemaphore)
                {
                    Log.Info("Done with DisconnectInternal(), relasing writing semaphore");
                    m_writingSemaphore.Release();
                }
            }
        }
        /// <summary>
        /// Connects to the server with the provided details
        /// </summary>
        /// <param name="nick">Nickname to use</param>
        /// <param name="user">Username to use</param>
        /// <param name="realname">Real name to use</param>
        /// <param name="host">Host to which to connect</param>
        /// <param name="port">Port on which to connect</param>
        /// <param name="password">Password to send on connect</param>
        /// <returns></returns>
        public async Task ConnectAsync(String nick, String user, String realname, String host, int port = 6667, String password = null)
        {
            Log.Info("Beginning connect");
            bool hasWritingSemaphore = false;

            // Take the writing semaphore so writes do not proceed until the connection is stable
            await m_writingSemaphore.WaitAsync();
            hasWritingSemaphore = true;

            try
            {
                // Reset state
                lock (m_registrationMutex)
                {
                    DisconnectInternal(hasWritingSemaphore); // Resets connection, resets timers, resets reading thread
                    Nick = nick; 
                    Username = user;
                    RealName = realname; 
                    Host = host;
                    Port = port;
                    Password = password;
                    TCP = new TcpClient();
                }

                try
                {
                    await TCP.ConnectAsync(host, port);
                } catch (Exception ex)
                {
                    Exception = ex;
                    RaiseEvent(OnException, this, ex);
                    throw ex; // If connect failed, we want to make sure the caller knows about it
                }

                lock (m_registrationMutex)
                {
                    // If connect succeeded
                    Log.Info("TCP connect succeeded.");
                    Connected = true;
                }

                // Setup reader and writer
                Log.Info("Creating stream writing and signalling reader. Have writing semaphore = {0}", hasWritingSemaphore);
                m_streamWriter = new StreamWriter(TCP.GetStream(), Encoding);
                m_readingThread.Signal();

                // We must release writing semaphore so that the registration functions can write
                if (hasWritingSemaphore)
                {
                    m_writingSemaphore.Release();
                    hasWritingSemaphore = false;
                }

                lock (m_registrationMutex)
                {
                    Debug.Assert(Connected); 
                    if (Connected)
                    {
                        RegisterWithServer();
                        // Restart the timer for detecting timeout
                        lock (m_timeoutTimer)
                        {
                            Log.Info("Starting timeout timer");
                            m_timeoutTimer.Change(Timeout, Timeout);
                        }

                        // Restart the itmer for sending PINGs
                        lock (m_pingTimer)
                        {
                            Log.Info("Starting ping timer");
                            m_pingTimer.Change(TimeSpan.FromMilliseconds(Timeout.TotalMilliseconds / 2), TimeSpan.FromMilliseconds(Timeout.TotalMilliseconds / 2));
                        }
                    }
                }

            }
            finally
            {
                Log.Info("Done with connection, Connected={0} hasWritingSemaphore={1}", Connected, hasWritingSemaphore);
                if (hasWritingSemaphore)
                {
                    m_writingSemaphore.Release();
                    hasWritingSemaphore = false;
                }
            }
        }
        /// <summary>
        /// Sends an EOL-terminated message to the server (\n is appended by this method)
        /// </summary>
        /// <param name="format">Format of message to send</param>
        /// <param name="formatParameters">Format parameters</param>
        /// <returns>True if sending was successful, otherwise false - should only happen on a networking error</returns>
        public async Task<bool> SendRawMessageAsync(String format, params String[] formatParameters)
        {
            return await SendRawMessageAsync(String.Format(format, formatParameters));
        }
        /// <summary>
        /// Sends an EOL-terminated message to the server (\n is appended by this method)
        /// </summary>
        /// <param name="format">Message to send</param>
        /// <returns>True if sending was successful, otherwise false - should only happen on a networking error</returns>
        public async Task<bool> SendRawMessageAsync(String message)
        {
            // Apply all outgoing filters in order
            lock (m_filtersOutgoing)
            {
                foreach (var filter in m_filtersOutgoing)
                {
                    try
                    {
                        message = filter.FilterMessage(this, message);
                    }
                    catch (Exception e)
                    {
                        RaiseEvent(OnException, this, e);
                        Debug.Assert(false, "Exception filtering outgoing message.", e.Message);
                    }
                }
            }
            try
            {
                await m_writingSemaphore.WaitAsync();

                await m_streamWriter.WriteLineAsync(message);

                await m_streamWriter.FlushAsync();
            }
            catch (Exception e)
            {
                Exception = e;
                RaiseEvent(OnException, this, e);
                return false;
            }
            finally 
            { 
                m_writingSemaphore.Release(); 
            }

            RaiseEvent(OnRawMessageSent, this, message);

            return true;
        }

        /// <summary>
        /// Registers with the server (sends PASS, NICK, USER) (synchronous)
        /// </summary>
        private void RegisterWithServer()
        {
            Debug.Assert(Connected);
            Log.Info("Registering synchronously with the server");
            lock (m_registrationMutex)
            {
                Log.Info("Registered={0}", Registered);
                if (Registered) return;
                if (!String.IsNullOrEmpty(Password))
                {
                    SendRawMessageAsync("PASS {0}", Password).Wait();
                }
                SendRawMessageAsync("NICK {0}", Nick).Wait();
                SendRawMessageAsync("USER {0} 0 * :{1}", Username, RealName).Wait();
                Registered = true;
            }
        }
        #endregion IO


        #region Internal Handlers
        private void TimeoutTimerElapsedHandler(Object state)
        {
            lock (m_timeoutTimer)
            {
                if ((DateTime.Now - LastMessageTime) > Timeout)
                {
                    DisconnectInternal();
                    RaiseEvent(OnTimeout, this);
                }
            }
        }
        private void PrivmsgHandler(IrcClient sender, String message)
        {
            String[] words = message.Split(' ');
            if (words.Length >= 4 && OnRfcPrivmsg != null && words[1].Equals("PRIVMSG", StringComparison.CurrentCultureIgnoreCase))
            {
                String source = words[0];
                String target = words[2];
                String text = message.Substring(message.IndexOf(":", 1) + 1);
                UpdateFullAddress(target, source);
                RaiseEvent(OnRfcPrivmsg, this, source, target, text);
                
            }
        }
        /// <summary>
        /// A IrcRawMessageHandler for handling ERROR responses
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="message"></param>
        private void ErrorHandler(Object sender, String message)
        {
            if (OnRfcError != null && message.StartsWith("ERROR"))
            {
                RaiseEvent(OnRfcError, this, message.Substring(message.IndexOf(":") + 1));    
            }
        }
        /// <summary>
        /// Handler for PINGs from the servver
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="message"></param>
        private void PingHandler(Object sender, String message)
        {
            if (message.StartsWith("PING"))
            {
                if (message.Length > "PING ".Length)
                {
                    var pongTask = SendRawMessageAsync("PONG {0}", message.Substring("PING ".Length));
                }
                else
                {
                    var pongTas = SendRawMessageAsync("PONG :No challenge was received");
                }
            }
        }
        /// <summary>
        /// Handler for numeric events
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="message"></param>
        private void NumericHandler(Object sender, String message)
        {
            // FORMAT :<server name> <numeric> <target> :<other>
            var words = message.Split(' ');
            if (words.Length >= 3)
            {
                int numeric;
                if (int.TryParse(words[1], out numeric))
                {
                    ieOnNumericReceived(this, words[0], numeric, words[2], words.Length > 3 ? message.Substring(message.IndexOf(words[2]) + words[2].Length + 1) : null);
                }
            }

        }
        #endregion Internal Handlers

        /// <summary>
        /// Raises events with the specified threadding model (see the SingleThreadedEvents property)
        /// Exceptions are eaten
        /// </summary>
        /// <param name="del">The delegate to invoke</param>
        /// <param name="parameters">Parameters for the object</param>
        private void RaiseEvent(Delegate del, params object[] parameters)
        {
            if (del == null)
            {
                return;
            }

            if (SingleThreadedEvents)
            {
                try
                {
                    del.DynamicInvoke(parameters);
                }
                catch (Exception) 
                {
                    // Nothing, let's not break the reader thread because of some silly ass event handler
                    Debug.Assert(false, "Exception in event handler");
                }
            }
            else
            {
                foreach (var d2 in del.GetInvocationList())
                {
                    Task.Run(() => { d2.DynamicInvoke(parameters); });
                }
            }
        }

        #region Events and Delegates

        public delegate void IrcExceptionHandler(IrcClient sender, Exception exception);
        /// <summary>
        /// Called when an exception is called by an IRC method, such as failure to connect.
        /// </summary>
        public event IrcExceptionHandler OnException;

        public delegate void IrcRawMessageHandler(IrcClient sender, String message);
        /// <summary>
        /// Called when any EOL-terminated message is received on the TcpClient
        /// </summary>
        public event IrcRawMessageHandler OnRawMessageReceived;
        /// <summary>
        /// Called when any message is successfully sent on the TcpClient
        /// </summary>
        public event IrcRawMessageHandler OnRawMessageSent;

        public delegate void RfcOnErrorHandler(IrcClient sender, String message);
        /// <summary>
        /// Called when ERROR is received from the server
        /// </summary>
        public event RfcOnErrorHandler OnRfcError;

        public delegate void RfcNumericHandler(IrcClient sender, String source, int numeric, String target, String other);
        /// <summary>
        /// Called when an RFC Numeric is received from the server
        /// </summary>
        public event RfcNumericHandler OnRfcNumeric;

        /// <summary>
        /// Called when RFC Numeric 001 is received, to confirm we are both connected and registered.
        /// It is STRONGLY recommended you add a delay to any processing here, especially for channel joining (so we have time to get other numerics)
        /// </summary>
        public event Action<IrcClient> OnConnect;

        /// <summary>
        /// Called when the socket cannot be read from, indicating a disconnect
        /// </summary>
        public event Action<IrcClient> OnDisconnect;

        public delegate void RfcPrivmsgHandler(IrcClient sender, String source, String target, String message);
        /// <summary>
        /// Called when a PRIVMSG is received from the server
        /// </summary>
        public event RfcPrivmsgHandler OnRfcPrivmsg;

        public delegate void RfcNamesReplyHandler(IrcClient sender, String target, String list);


        /// <summary>
        /// Called when a NAMES reply (numeric 353) is received from the server
        /// </summary>
        public event RfcNamesReplyHandler OnNamesReply;

        public delegate void RfcISupport(IrcClient sender, Dictionary<String, String> parameters);
        /// <summary>
        /// Called when an ISupport (numeric 005) is received from the server
        /// </summary>
        public event RfcISupport OnISupport;

        public delegate void RfcJoinHandler(IrcClient sender, String sourceAddress, String channel);
        /// <summary>
        /// Called when a JOIN is received from the server
        /// </summary>
        public event RfcJoinHandler OnRfcJoin;

        public delegate void RfcPartHandler(IrcClient sender, String sourceAddress, String channel, String reason);
        /// <summary>
        /// Called when a PART is received from the server
        /// </summary>
        public event RfcPartHandler OnRfcPart;


        public delegate void RfcKickHandler(IrcClient sender, String source, String target, String channel, String reason);
        /// <summary>
        /// Called when a KICK is received from the server
        /// </summary>
        public event RfcKickHandler OnRfcKick;

        public delegate void RfcModeHandler(IrcClient sender, String source, String target, String modes);
        /// <summary>
        /// Called when a MODE is received from the server
        /// </summary>

        public event RfcModeHandler OnRfcMode;

        public delegate void RfcNickHandler(IrcClient sender, String source, String nick);
        /// <summary>
        /// Triggered when a user changes his nickname (a NICK is received)
        /// </summary>
        public event RfcNickHandler OnRfcNick;

        public delegate void RfcQuitHandler(IrcClient sender, String source, String message);
        /// <summary>
        /// Triggered when a user QUITs (a QUIT is received)
        /// </summary>
        public event RfcQuitHandler OnRfcQuit;

        /// <summary>
        /// Called when LastMessage was more than Timeout ago
        /// </summary>
        public event Action<IrcClient> OnTimeout;
        #endregion Events and Delegates

        /// <summary>
        /// Returns the channel with this name, or null if client is not in the channel
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public Channel GetChannel(String name)
        {
            Channel c = null;
            lock (m_channels)
            {
                if (!m_channels.TryGetValue(name.ToLower(), out c))
                {
                    return null;
                }
            }
            return c;
        }

        /// <summary>
        /// Parses fulladdress and updates the members of ChannelUser with the data
        /// </summary>
        /// <param name="channel">Channel this user is on</param>
        /// <param name="fulladdress">Full address</param>
        private void UpdateFullAddress(String channel, String fulladdress)
        {
            String nick = ChannelUser.GetNickFromFullAddress(fulladdress);
            String user = ChannelUser.GetUserFromFullAddress(fulladdress);
            String host = ChannelUser.GetHostFromFullAddress(fulladdress);

            if (nick != null && user != null && host != null)
            {
                lock (m_channels)
                {
                    Channel c = GetChannel(channel);
                    // C could be null if our caller didn't check to see if it were really a channel
                    if (c != null)
                    {
                        ChannelUser cu = c.GetUser(nick.ToLower());
                        // CU could be null as server's can set modes, and such
                        if (cu != null)
                        {
                            cu.Username = user;
                            cu.Host = host;
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            DisconnectInternal(false);
            if (m_readingThread != null)
            {
                m_readingThread.Kill();
                m_readingThread = null;
            }
            if (m_channels != null)
            {
                m_channels.Clear();
                m_channels = null;
            }
            if (m_channelPrefixMap != null)
            {
                m_channelPrefixMap.Clear();
                m_channelPrefixMap = null;
            }
            if (m_filtersIncoming != null)
            {
                m_filtersIncoming.Clear();
                m_filtersIncoming = null;
            }
            if (m_filtersOutgoing != null)
            {
                m_filtersOutgoing.Clear();
                m_filtersOutgoing = null;
            }
        }
    }
    /// <summary>
    /// Represents a Channel on the iRC server
    /// </summary>
    public class Channel
    {
        /// <summary>
        /// The name of this channel
        /// </summary>
        public String Name { get; private set; }
        /// <summary>
        /// A map of lower(nick) -> channeluser, for every user in the channel.  This is essentially just a list of users, but the map makes for an easy lookup
        /// </summary>
        public IDictionary<String, ChannelUser> Users { get { return _users; } }

        private IDictionary<String, ChannelUser> _users = new ConcurrentDictionary<String, ChannelUser>();

        /// <summary>
        /// Gets a user by his name or fulladdress
        /// </summary>
        /// <param name="nameOrFullAddress"></param>
        /// <returns>The channel user, or null if there is no matching user in this channel</returns>
        public ChannelUser GetUser(string nameOrFullAddress)
        {
            ChannelUser result = null;
            lock (_users)
            {
                if (!_users.TryGetValue(ChannelUser.GetNickFromFullAddress(nameOrFullAddress).ToLower(), out result))
                {
                    return null;
                }
            }
            return result;
        }

        internal Channel(String name)
        {

            Name = name;
        }

        /// <summary>
        /// Derived from the Name
        /// </summary>
        /// <returns></returns>
        public override int GetHashCode()
        {
            return Name.GetHashCode();
        }
        /// <summary>
        /// Channel equality is defined as 2 channels which have the same case-insitive name
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        public override bool Equals(object obj)
        {
            return obj is Channel && ((Channel)obj).Name.Equals(Name, StringComparison.CurrentCultureIgnoreCase);
        }
    }
    /// <summary>
    /// Represents a guest of a channel and their basic state. This represents a unique (User, Channel) pair and meant for reading only
    /// </summary>
    public class ChannelUser
    {
        /// <summary>
        /// The user's nick
        /// </summary>
        public String Nick { get; set; }
        /// <summary>
        /// The user's username
        /// </summary>
        public String Username { get; set; }
        /// <summary>
        /// The user's host
        /// </summary>
        public String Host { get; set; }
        /// <summary>
        /// The address of the user in format Nick!Username@Host
        /// </summary>
        public String FullAddress { get { return Nick == null || Username == null || Host == null ? null : String.Format("{0}!{1}@{2}", Nick, Username, Host); } }

        /// <summary>
        /// Checks to see if the user has at least the given prefix SYMBOL (true if his highest prefix is higher or equal to this prefix)
        /// </summary>
        /// <param name="svr">Server info from the IRC Client containing prefix information</param>
        /// <param name="prefix">Prefix to compare against</param>
        /// <returns></returns>
        public bool AtLeast(IrcClient.ServerInfoType svr, char prefix)
        {
            int targetPosition = svr.PREFIX_symbols.IndexOf(prefix);
            if (targetPosition <= 0 || Prefixes.Length == 0) return false;
            int myPosition = svr.PREFIX_symbols.IndexOf(Prefixes[0]);
            if (myPosition < 0) return false;
            return myPosition <= targetPosition;
        }

        /// <summary>
        /// The prefixes this user has in the channel, from most powerful to least
        /// </summary>
        public String Prefixes
        {
            get { lock (m_prefixes) { return m_prefixes.ToString(); } }
        }

        /// <summary>
        /// The prefixes this user has on this channel
        /// </summary>
        private StringBuilder m_prefixes = new StringBuilder("");

        /// <summary>
        /// Creates a new Channel User
        /// </summary>
        /// <param name="nickOrFullAddress">The nick or full address of a user</param>
        /// <param name="channel">The channel this user is in</param>
        public ChannelUser(String nickOrFullAddress, Channel channel)
        {
            if (nickOrFullAddress.Contains('!') && nickOrFullAddress.Contains('@') && nickOrFullAddress.IndexOf('@') > nickOrFullAddress.IndexOf('!'))
            {
                Nick = GetNickFromFullAddress(nickOrFullAddress);
                Username = GetUserFromFullAddress(nickOrFullAddress);
                Host = GetHostFromFullAddress(nickOrFullAddress);

            }
            else { Nick = nickOrFullAddress; }
        }
        /// <summary>
        /// Gets the nickname portion of a fulladdress, such as NICK!user@host
        /// </summary>
        /// <param name="fulladdress">Full address in format nick!user@host</param>
        /// <returns>The nick portion of the full address, or fulladdress</returns>
        public static String GetNickFromFullAddress(String fulladdress)
        {
            if (!fulladdress.Contains('!'))
            {
                return fulladdress;
            }
            return fulladdress.Substring(0, fulladdress.IndexOf('!')).Replace(":", "");
        }
        /// <summary>
        /// Gets the user portion of a fulladdress, such as nick!USER@host
        /// </summary>
        /// <param name="fulladdress">Full address in format nick!user@host</param>
        /// <returns>The user portion of the fulladdress, or null</returns>
        public static String GetUserFromFullAddress(String fulladdress)
        {
            if (!fulladdress.Contains('!') || !fulladdress.Contains('@') || fulladdress.IndexOf('@') < fulladdress.IndexOf('!'))
            {
                return null;
            }

            int start = fulladdress.IndexOf('!') + 1;
            return fulladdress.Substring(start, fulladdress.IndexOf('@') - start);
        }

        /// <summary>
        /// Gets the nickname portion of a fulladdress, such as nick!user@HOST
        /// </summary>
        /// <param name="fulladdress">Full address in format nick!user@host</param>
        /// <returns>The host portion of the full address, or null</returns>
        public static String GetHostFromFullAddress(String fulladdress)
        {
            if (!fulladdress.Contains('@') || fulladdress.IndexOf('@') == fulladdress.Length-1)
            {
                return null;
            }
            return fulladdress.Substring(fulladdress.IndexOf('@') + 1);
        }

        /// <summary>
        /// Inserts a prefix (mode symbol) into this client's prefix list for the given channel and svrInfo class (must have PREFIX_symbols set by server)
        /// </summary>
        /// <param name="svrInfo">Struct representing information about the server. Set automatically when we receive ISUPPORT from the server</param>
        /// <param name="prefix">The prefix to insert to this user's prefix list</param>
        internal void InsertPrefix(IrcClient.ServerInfoType svrInfo, char prefix)
        {
            Debug.Assert(svrInfo.PREFIX_symbols != null, "svrInfo.PREFIX_symbols is null - it should have been set when we received ISUPPORT from the server.  It is not possible to maintain a prefix list without this information");
            Debug.Assert(svrInfo.PREFIX_symbols.Contains(prefix), "svrInfo.PREFIX_symbols is non-null but does not contain the prefix that was inserted", "Prefix: {0}", prefix);
            lock (m_prefixes)
            {
                if (m_prefixes.ToString().Contains(prefix))
                {
                    return;
                }
                else if (m_prefixes.Length == 0)
                {
                    m_prefixes.Append(prefix);
                }
                else
                {
                    /// Find the first prefix in the current list (newList) whose value is less than this new prefix, and insert at that position
                    /// Or append it to the end if we never find one
                    for (int i = 0; i < m_prefixes.Length; ++i)
                    {
                        if (svrInfo.PREFIX_symbols.IndexOf(prefix) < svrInfo.PREFIX_symbols.IndexOf(m_prefixes[i]))
                        {
                            m_prefixes.Insert(i, prefix);
                            break;
                        }
                        else if (i + 1 == m_prefixes.Length) // If we've reached the end and still haven't found one of lower value, then this one belongs at the end
                        {
                            m_prefixes.Append(prefix);
                            return;
                        }
                    }
                }
            }
        }
        /// <summary>
        /// Deletes a prefix (mode symbol) from this client's prefix list if it exists in the list
        /// </summary>
        /// <param name="channelName"></param>
        /// <param name="prefix"></param>
        internal void DeletePrefix(char prefix)
        {
            lock (m_prefixes)
            {
                int prefixPosition = m_prefixes.ToString().IndexOf(prefix);
                if (prefixPosition >= 0)
                {
                    m_prefixes.Remove(prefixPosition, 1);
                }
            }
        }
    }
    /// <summary>
    /// The integer interpreted by mIRC as a color when following ASCII character 0x03
    /// </summary>
    public enum mIRCColor
    {
        WHITE = 0,
        BLACK = 1,
        DARK_BLUE = 2,
        DARK_GREEN = 3,
        RED = 4,
        DARK_RED = 5,
        DARK_PURPLE = 6,
        ORANGE = 7,
        GOLD = 8,
        GREEN = 9,
        CYAN = 10,
        TEAL = 11,
        BLUE = 12,
        PINK = 13,
        DARK_GRAY = 14,
        GRAY = 15,
    }

    /// <summary>
    /// Manages the Irc thread which deals directly with the input stream. When signaled it begins trying to read from the input stream for the given IrcClient and raising events.  If it fails, it raises an exception and waits for another signal.
    /// </summary>
    internal class IrcReader
    {
        /// <summary>
        /// The IrcClient whose socket is being read
        /// </summary>
        private IrcClient Client { get; set; }
        // The thread will always be active as long as reference remains
        // This semaphore is used to block the thread while notthing can be read
        private SemaphoreSlim m_semaphore = new SemaphoreSlim(0, 1);

        private bool m_alive = true;

        // When set to false, will stop trying to read but the thread will persist.
        private bool m_reading = true;

        /// <summary>
        /// Creates a new reader.  Only makes sense to do this once for each IRC instance.
        /// </summary>
        /// <param name="client"></param>
        public IrcReader(IrcClient client)
        {
            Debug.Assert(client != null);
            Client = client;
            new Thread(ThreadStart).Start();
        }

        /// <summary>
        /// When a message is received on the client's socket
        /// </summary>
        public event Action<IrcReader, String> OnRawMessageReceived;
        /// <summary>
        /// When an exception occurs trying to read from the client's socket
        /// </summary>
        public event Action<IrcReader, Exception> OnException;

        private Thread m_thread = null;

        /// <summary>
        /// Thread body
        /// </summary>
        void ThreadStart()
        {
            m_thread = Thread.CurrentThread;
            // Outter loop stays alive
            while (m_alive)
            {
                try
                {
                    Log.Info("Reader thread is waiting to run");
                    m_semaphore.Wait();
                    Log.Info("Reader thread has been unblocked");
                    if (!m_alive)
                    {
                        Log.Info("Reader thread is exiting gracefully");
                        break;
                    }
                  
                    if (Client == null || Client.TCP == null)
                    {
                        Log.Info("Reader thread was unblocked but clien is not read to read");
                        // Nope, try again.
                        continue;
                    }

                    try
                    {
                        using (StreamReader reader = new StreamReader(Client.TCP.GetStream()))
                        {
                            m_reading = true;
                            while (Client.TCP.Connected && m_reading)
                            {
                                String line = reader.ReadLine();
                                if (line != null && OnRawMessageReceived != null)
                                {
                                    try
                                    {
                                        OnRawMessageReceived(this, line);
                                    }
                                    catch (Exception e)
                                    {
                                        // For exceptions by event handlers, don't exit the loop but raise an exception
                                        // If exception event handlers raise another exception...that's their own problem.
                                        if (OnException != null)
                                        {
                                            OnException(this, e);
                                        }
                                    }
                                }
                                else if (line == null)
                                {
                                    Log.Info("Reader encountered EOF");
                                    // Catch this outside the reader loop but inside the thread loop
                                    throw new EndOfStreamException();
                                }
                            }
                        }
                    }
                    catch (ThreadInterruptedException) { Log.Info("Reader thread was interrupted."); /* Allow interrupts to break us out of the read-wait loop but not the entire thread */ }
                    catch (Exception e)
                    {
                        Log.Info("Reader thread encountered an unexpected exception");
                        if (OnException != null)
                        {
                            OnException(this, e);
                        }
                    }
                }
                catch (ThreadInterruptedException) { Log.Info("Reader thread was interrupted while blocked. m_alive={0}",m_alive); /* If interrupted, it's either ReleaseStream (go back and wait) or its Kill (go back and find m_alive to be false) */ }
            } 
        }

        /// <summary>
        /// Allows the thread to die gracefully
        /// If it is currently blocked waiting on information to read, it will stay until unblocked
        /// </summary>
        public void Die()
        {
            Log.Info("Request to die.");
            m_alive = false;
        }

        /// <summary>
        /// Combines Die()'s graceful closure with raising an interruption to stop any current blocking task
        /// </summary>
        public void Kill()
        {
            Log.Info("Killing reader thread");
            Die();
            if (m_thread != null)
            {
                Log.Info("Interrupting reader thread");
                m_thread.Interrupt();
            }
        }

        /// <summary>
        /// If the thread is currently blocked waiting on a read, this will stop it. 
        /// </summary>
        public void ReleaseStream()
        {
            Log.Info("Interruping reader");
            if (m_thread != null && m_thread.ThreadState == System.Threading.ThreadState.WaitSleepJoin)
            {
                Log.Info("Blocked reader found, interrupting...");
                m_reading = false;
                m_thread.Interrupt();
            }
            else
            {
                Log.Info("Reader is not busy");
            }
        }

        /// <summary>
        /// Signals the thread to wake up and start checking for message on the associated Client's socket
        /// </summary>
        /// <returns>True if thread woke up, false if thread was not asleep</returns>
        public bool Signal()
        {
            try
            {
                Log.Info("Signalling reader thread.");
                m_semaphore.Release();
                return true;
            } catch (SemaphoreFullException)
            {
                Log.Info("Reader is not blocked");
                return false;
            }
        }
    }

    /// <summary>
    /// Implement to filter raw messages before they are processed or sent
    /// </summary>
    public interface IIrcMessageFilter
    {
        /// <summary>
        /// Called before a message is processed, or before a message is sent, to allow implementers to modify the message
        /// </summary>
        /// <param name="sender">The client</param>
        /// <param name="message"></param>
        /// <returns>null to block message or a string with which to replace it</returns>
        String FilterMessage(IrcClient sender, String message);
    }

    public class Log
    {
        private static Object m_Lock = new Object();
        public static void Info(String format, params object[] parms)
        {
            lock (m_Lock)
            {
                // TODO Modular logging
                var color = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(DateTime.Now + " " + format, parms);
                Console.ForegroundColor = color;
            }
            
        }
    }
}