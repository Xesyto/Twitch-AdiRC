﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using Timer = System.Threading.Timer;
using Twitch___AdiIRC.TwitchApi;
using AdiIRCAPI;
using Twitch___AdiIRC;


namespace TwitchAdiIRC
{
    public class TwitchAdiIrc : IPlugin
    {
        public string Description => "Provides simple additional features like emotes for twitch chat integration.";
        public string Author => "Xesyto";
        public string Name => "Twitch @ AdiIRC";
        public string Version => "3.1";
        public string Email => "";

        public IPluginHost Host { get; set; }
        public ITools Tools { get; set; }
        
        private readonly string _emoteDirectory =  Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\AdiIRC\TwitchEmotes";
        private Timer _topicTimer;

        private List<string> _handledEmotes;
        private IServer _twitchServer;
        private Settings _settings;        
        private SettingsForm _settingsForm;


        public void Initialize()
        {
            //Register Delegates. 
            Host.OnRawData += MyHostOnOnRawData;
            Host.OnJoin += HostOnOnJoin;
            Host.OnCommand += HostOnOnCommand;

            _handledEmotes = new List<string>();
            _topicTimer = new Timer(state => TopicUpdate(),true, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(10));

            if (File.Exists(Settings.FullPath))
            {
                _settings = Settings.Load();
            }
            else
            {
                _settings = new Settings();
            }

            _settingsForm = new SettingsForm(_settings);

            Host.HookCommand(this, "/twitch@", "configure twitch @ adiirc",
                "show a window that lets you change twitch # adiirc settings");
            

            if (!Directory.Exists(_emoteDirectory))
            {
                Directory.CreateDirectory(_emoteDirectory);
            }
        }

        private void HostOnOnCommand(object window, string command, string args)
        {            
            _settingsForm.Show();            
        }

        private void HostOnOnJoin(IServer server, IChannel channel, IUser user, out EatData @return)
        {
            @return = EatData.EatNone;

            CheckTwitchServer();

            if (!server.Network.ToLower().Contains("twitch"))                            
                return;
            
            if (user.Nick != _twitchServer.UserNick)
                return;

            try
            {
                var userName = channel.Name.TrimStart('#');
                var topicData = TwitchApiTools.GetSimpleChannelInformationByName(userName);
                
                var topicMessage = $":Twitch!Twitch@Twitch.tv TOPIC {channel.Name} :{topicData}";
                _twitchServer.SendFakeRaw(topicMessage);
            }
            catch (Exception)
            {
                Host.SendCommand(_twitchServer, ".echo", "Error updating channel topic.");                
            }
            
        }

        private void MyHostOnOnRawData(object sender, RawDataArgs rawDataArgs)
        {
            if (CheckTwitchServer() == false)
                return;

            var dataString = System.Text.Encoding.UTF8.GetString(rawDataArgs.Bytes);

            //Process Message
            if (dataString.Contains("PRIVMSG ")) 
            {
                //Handle Subscriptions ( not resubs, also prime subs )
                var primeRegex = @"twitchnotify!twitchnotify@twitchnotify.tmi.twitch.tv PRIVMSG (#\S+) :(.*subscribed.*)";
                var primeMatch = Regex.Match(dataString, primeRegex);

                if (_settings.ShowSubs && primeMatch.Success)
                {
                    var channel = primeMatch.Groups[1].ToString();
                    var message = primeMatch.Groups[2].ToString();

                    var notice = $":Twitch!Twitch@Twitch.tv NOTICE {channel} :{message}";
                    _twitchServer.SendFakeRaw(notice);

                    rawDataArgs.Bytes = null;
                    return;
                }


                TwitchIrcMessage twitchMessage;

                try
                {
                    twitchMessage = new TwitchIrcMessage(dataString);
                }
                catch (Exception)
                {
                    return;
                }

                //Handle Emotes
                if (twitchMessage.HasEmotes)
                {                    
                    RegisterEmotes(twitchMessage.Emotes);
                }

                //Handle Bits
                if (_settings.ShowCheers && twitchMessage.Tags.ContainsKey("bits"))
                {
                    if (RegisterBits(twitchMessage.Tags["bits"]) )
                    {                        
                        var emoteName = "cheer" + twitchMessage.Tags["bits"];
                        var bitsMessage = twitchMessage.Tags["bits"] + " bits";
                        
                        var notice = $":Twitch!Twitch@Twitch.tv NOTICE {twitchMessage.Channel} :{twitchMessage.UserName} {emoteName} just cheered for {bitsMessage}! {emoteName}";
                        _twitchServer.SendFakeRaw(notice);
                    }
                }

                return;
            }

            //Redirect notices to proper channel tab
            if (dataString.Contains("NOTICE "))
            {
                //Check if its a usable notiec message
                var noticeRegex = @".+ :tmi.twitch.tv NOTICE (#.+) :(.+)";
                var noticeMatch = Regex.Match(dataString, noticeRegex);

                if (noticeMatch.Success)
                {
                    var channel = noticeMatch.Groups[1].ToString();
                    var message = noticeMatch.Groups[2].ToString();

                    //Send a fake regular irc notice instead
                    
                    var notice = $":Twitch!Twitch@Twitch.tv NOTICE {channel} :{message}";
                    _twitchServer.SendFakeRaw(notice);

                    //Eat message.
                    rawDataArgs.Bytes = null;
                    return;
                }
            }

            //Handle timeout/ban messages
            if (dataString.Contains("CLEARCHAT ") )
            {
                rawDataArgs.Bytes = null;

                if (!_settings.ShowTimeouts)
                    return;

                var clearChatRegex = @"@ban-duration=(.*?);ban-reason=(.*?);.+ :tmi.twitch.tv CLEARCHAT (#.+) :(.+)";
                var clearChatMatch = Regex.Match(dataString, clearChatRegex);

                if (clearChatMatch.Success)
                {
                    var channel = clearChatMatch.Groups[3].ToString();
                    var message = clearChatMatch.Groups[2].ToString().Replace("\\s"," ");
                    var time = clearChatMatch.Groups[1].ToString();
                    var target = clearChatMatch.Groups[4].ToString();

                    if (string.IsNullOrEmpty(time))
                        time = "∞";

                    if (string.IsNullOrEmpty(message))
                        message = "No Reason Given";

                    var notice = $":Twitch!Twitch@Twitch.tv NOTICE {channel} :{target} was banned: {message} [{time} seconds]";
                    _twitchServer.SendFakeRaw(notice);

                    return;
                }                
            }

            //Handle resubscriptions
            if (dataString.Contains("USERNOTICE "))
            {
                rawDataArgs.Bytes = null;

                if (!_settings.ShowSubs)
                    return;

                var subRegexMessage = @"system-msg=(.*?);.*USERNOTICE (#.+) :(.*)";
                var subRegexNoMessage = @"system-msg=(.*?);.*USERNOTICE (#.+)";

                var subMessageMatch = Regex.Match(dataString, subRegexMessage);
                var subNoMessageMatch = Regex.Match(dataString, subRegexNoMessage);
                
                if (subMessageMatch.Success)
                {
                    var channel = subMessageMatch.Groups[2].ToString();
                    var systemMessage = subMessageMatch.Groups[1].ToString();
                    var userMessage = subMessageMatch.Groups[3].ToString();

                    systemMessage = systemMessage.Replace("\\s", " ");

                    var notice = $":Twitch!Twitch@Twitch.tv NOTICE {channel} :{systemMessage} [{userMessage}]";
                    _twitchServer.SendFakeRaw(notice);
                }else if (subNoMessageMatch.Success)
                {
                    var channel = subMessageMatch.Groups[2].ToString();
                    var systemMessage = subMessageMatch.Groups[1].ToString();

                    systemMessage = systemMessage.Replace("\\s", " ");

                    var notice = $":Twitch!Twitch@Twitch.tv NOTICE {channel} :{systemMessage}";
                    _twitchServer.SendFakeRaw(notice);
                }
            }

            if (dataString.Contains("ROOMSTATE ") || dataString.Contains("USERSTATE ") || dataString.Contains("CLEARCHAT "))
            {
                //Silently eat these messages and do nothing. They only cause empty * lines to appear in the server tab and Twitch@AdiIRC does not use them
                rawDataArgs.Bytes = null;
            }                                    
        }

        private void TopicUpdate()
        {
            if (CheckTwitchServer() == false)
                return;

            var channels = _twitchServer.GetChannels;
            
            foreach (IChannel channel in channels)
            {                
                try
                {
                    var channelName = channel.Name.TrimStart('#');
                    var newTopic = TwitchApiTools.GetSimpleChannelInformationByName(channelName);
                    if (channel.Topic != newTopic)
                    {
                        var topicMessage = $":Twitch!Twitch@Twitch.tv TOPIC #{channelName} :{newTopic}";
                        _twitchServer.SendFakeRaw(topicMessage);
                    }
                }
                catch (Exception)
                {
                    Host.SendCommand(_twitchServer, ".echo", "Error updating channel topics.");
                    return;
                }
            }
        }

        public void RegisterEmote(TwitchEmote emote)
        {
            if (_handledEmotes.Contains(emote.Name))
                return;

            var emoteFile = $"{_emoteDirectory}\\{emote.Id}.png";

            if (File.Exists(emoteFile))
            {
                
                //Actually register the emote with AdiIRC
                var command = $"Emoticons Emoticon_{emote.Name} {emoteFile}";
                Host.SendCommand(_twitchServer, ".setoption", command);

                _handledEmotes.Add(emote.Name);
                return;
            }

            if (DownloadEmote(emote))
            {
                //Actually register the emote with AdiIRC
                var command = $"Emoticons Emoticon_{emote.Name} {emoteFile}";
                Host.SendCommand(_twitchServer, ".setoption", command);                

                _handledEmotes.Add(emote.Name);
            }
        }

        public void RegisterEmotes(IEnumerable<TwitchEmote> emotes)
        {
            foreach (var emote in emotes)
            {
                RegisterEmote(emote);
            }
        }

        public bool RegisterBits(string bitCount)
        {
            if (string.IsNullOrEmpty(bitCount))
                return false;
            
            var emoteName = "cheer" + bitCount;

            if (_handledEmotes.Contains(emoteName))
                return true;

            var emoteFile = $"{_emoteDirectory}\\{emoteName}.png";

            if (File.Exists(emoteFile))
            {
                //Actually register the emote with AdiIRC
                var command = $"Emoticons Emoticon_{emoteName} {emoteFile}";
                Host.SendCommand(_twitchServer, ".setoption", command);

                _handledEmotes.Add(emoteName);
                return true;
            }
            
            if (DownloadBits(bitCount))
            {
                //Actually register the emote with AdiIRC                
                var command = $"Emoticons Emoticon_{emoteName} {emoteFile}";
                Host.SendCommand(_twitchServer, ".setoption", command);

                _handledEmotes.Add(emoteName);
                return true;
            }

            return false;
        }

        private bool CheckTwitchServer()
        {
            if (_twitchServer != null)
            {
                if (_twitchServer.Network.ToLower().Contains("twitch"))
                {                    
                    return true;
                }
            }

            foreach (var server in ((IEnumerable<IServer>)Host.GetServers))
            {
                if (server.Network.ToLower().Contains("twitch"))
                {
                    _twitchServer = server;
                    return true;
                }
            }
            return false;
        }

        private bool DownloadEmote(TwitchEmote emote)
        {
            var filePath = $"{_emoteDirectory}\\{emote.Id}.png";

            try
            {
                WebClient wc = new WebClient();                
                wc.DownloadFile(emote.URL, filePath);
            }
            catch (Exception)
            {                
                return false;
            }

            return true;
        }

        private bool DownloadBits(string bitCount)
        {
            var emoteName = "cheer" + bitCount;
            var filePath = $"{_emoteDirectory}\\{emoteName}.png";
            var color = "gray";

            try
            {
                var ibitCount = int.Parse(bitCount);
                if (ibitCount > 10000)
                {
                    color = "red";
                }else if (ibitCount > 5000)
                {
                    color = "blue";
                }else if (ibitCount > 1000)
                {
                    color = "green";
                }else if (ibitCount > 100)
                {
                    color = "purple";
                }
            }
            catch (Exception)
            {
                return false;                
            }


            var url = $"https://static-cdn.jtvnw.net/bits/light/static/{color}/1";

            try
            {
                WebClient wc = new WebClient();
                wc.DownloadFile(url, filePath);
            }
            catch (Exception)
            {
                return false;
            }

            return true;
        }

        public void Dispose()
        {
            _settings.Save();        
        }        
    }
}
