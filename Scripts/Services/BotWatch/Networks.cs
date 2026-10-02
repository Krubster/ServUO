using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Services.BotWatch
{
    /// <summary>Why two accounts are linked.</summary>
    public class AccountLink
    {
        public string A;
        public string B;

        /// <summary>Addresses both accounts used, with the last time both were seen on it.</summary>
        public readonly Dictionary<string, DateTime> SharedAddresses = new Dictionary<string, DateTime>();

        /// <summary>Sessions of both accounts online at the same time from the same address.</summary>
        public int Concurrent;

        /// <summary>Sessions of both accounts that started and ended within the co-presence window.</summary>
        public int Coupled;

        public DateTime LastShared => SharedAddresses.Count > 0 ? SharedAddresses.Values.Max() : DateTime.MinValue;

        public bool IsLink => SharedAddresses.Count > 0 || Coupled >= Networks.CoPresenceMin;

        public string Other(string account)
        {
            return account == A ? B : A;
        }

        public string Describe()
        {
            List<string> parts = new List<string>();

            if (SharedAddresses.Count > 0)
                parts.Add(String.Format("{0} shared IP{1}, last {2}", SharedAddresses.Count, SharedAddresses.Count > 1 ? "s" : "", Networks.Ago(LastShared)));

            if (Concurrent > 0)
                parts.Add(String.Format("{0} concurrent sessions from one IP", Concurrent));

            if (Coupled > 0)
                parts.Add(String.Format("{0} sessions started and ended together", Coupled));

            return String.Join("; ", parts);
        }
    }

    public class AccountInfo
    {
        public string Account;
        public DateTime Created;
        public DateTime FirstSeen = DateTime.MaxValue;
        public DateTime LastSeen = DateTime.MinValue;
        public readonly List<string> Addresses = new List<string>();
        public readonly List<CharacterRecord> Characters = new List<CharacterRecord>();

        public bool IsFresh => Created != DateTime.MinValue && DateTime.UtcNow - Created < TimeSpan.FromDays(Ratings.FreshDays);
    }

    public class AccountNetwork
    {
        public int Id;
        public readonly List<string> Accounts = new List<string>();
        public readonly List<AccountLink> Links = new List<AccountLink>();
    }

    /// <summary>
    /// The account graph at one moment. Built on demand from BotWatch's own session log and
    /// address history; ServUO's account data is not used.
    /// </summary>
    public class NetworkGraph
    {
        public readonly Dictionary<string, AccountInfo> Accounts = new Dictionary<string, AccountInfo>();
        public readonly Dictionary<string, AccountNetwork> ByAccount = new Dictionary<string, AccountNetwork>();
        public readonly List<AccountNetwork> Networks = new List<AccountNetwork>();
        public readonly Dictionary<string, AccountLink> Links = new Dictionary<string, AccountLink>();

        public IEnumerable<AccountLink> LinksOf(string account)
        {
            return Links.Values.Where(l => l.IsLink && (l.A == account || l.B == account));
        }

        public AccountNetwork NetworkOf(string account)
        {
            return account != null && ByAccount.TryGetValue(account, out AccountNetwork n) ? n : null;
        }
    }

    public static class Networks
    {
        public static TimeSpan CoPresenceWindow { get; private set; }
        public static int CoPresenceMin { get; private set; }

        public static void Configure()
        {
            CoPresenceWindow = Config.Get("BotWatch.CoPresenceWindow", TimeSpan.FromMinutes(1));
            CoPresenceMin = Config.Get("BotWatch.CoPresenceMin", 3);
        }

        public static string Ago(DateTime utc)
        {
            if (utc == DateTime.MinValue)
                return "never";

            TimeSpan ago = DateTime.UtcNow - utc;

            if (ago.TotalHours < 1)
                return String.Format("{0:F0} min ago", Math.Max(0, ago.TotalMinutes));

            if (ago.TotalDays < 1)
                return String.Format("{0:F0}h ago", ago.TotalHours);

            return String.Format("{0:F0}d ago", ago.TotalDays);
        }

        private static string Key(string a, string b)
        {
            return String.CompareOrdinal(a, b) < 0 ? a + "\n" + b : b + "\n" + a;
        }

        private static AccountLink GetLink(NetworkGraph g, string a, string b)
        {
            string key = Key(a, b);

            if (!g.Links.TryGetValue(key, out AccountLink link))
            {
                bool ordered = String.CompareOrdinal(a, b) < 0;
                g.Links[key] = link = new AccountLink { A = ordered ? a : b, B = ordered ? b : a };
            }

            return link;
        }

        private static AccountInfo GetInfo(NetworkGraph g, string account)
        {
            if (!g.Accounts.TryGetValue(account, out AccountInfo info))
                g.Accounts[account] = info = new AccountInfo { Account = account };

            return info;
        }

        public static NetworkGraph Build()
        {
            NetworkGraph g = new NetworkGraph();

            // Accounts and their characters
            foreach (CharacterRecord rec in BotWatch.Characters.Values.Where(r => r.Account != null))
            {
                AccountInfo info = GetInfo(g, rec.Account);

                info.Characters.Add(rec);

                if (rec.AccountCreated != DateTime.MinValue)
                    info.Created = rec.AccountCreated;
            }

            // Addresses: every pair of accounts seen on the same address is linked.
            foreach (AddressRecord ar in BotWatch.Addresses.Values.Where(a => a.Account != null))
            {
                AccountInfo info = GetInfo(g, ar.Account);

                info.Addresses.Add(ar.Address);

                if (ar.FirstSeen < info.FirstSeen)
                    info.FirstSeen = ar.FirstSeen;

                if (ar.LastSeen > info.LastSeen)
                    info.LastSeen = ar.LastSeen;
            }

            foreach (var byAddress in BotWatch.Addresses.Values.Where(a => a.Account != null && a.Address != "?").GroupBy(a => a.Address))
            {
                List<AddressRecord> list = byAddress.ToList();

                for (int i = 0; i < list.Count; i++)
                {
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        if (list[i].Account == list[j].Account)
                            continue;

                        DateTime both = list[i].LastSeen < list[j].LastSeen ? list[i].LastSeen : list[j].LastSeen;

                        GetLink(g, list[i].Account, list[j].Account).SharedAddresses[byAddress.Key] = both;
                    }
                }
            }

            // Sessions: concurrent from one address, and coupled (start and end together).
            List<SessionRecord> sessions = BotWatch.Sessions.Where(s => s.Account != null).OrderBy(s => s.Start).ToList();

            for (int i = 0; i < sessions.Count; i++)
            {
                SessionRecord a = sessions[i];

                for (int j = i + 1; j < sessions.Count; j++)
                {
                    SessionRecord b = sessions[j];

                    if (b.Start > a.EndOrLastSeen)
                        break;

                    if (a.Account == b.Account)
                        continue;

                    AccountLink link = null;

                    if (a.Address == b.Address && a.Address != "?")
                    {
                        link = GetLink(g, a.Account, b.Account);
                        link.Concurrent++;
                    }

                    if (b.Start - a.Start <= CoPresenceWindow && !a.Open && !b.Open &&
                        (a.End - b.End).Duration() <= CoPresenceWindow)
                    {
                        link = link ?? GetLink(g, a.Account, b.Account);
                        link.Coupled++;
                    }
                }
            }

            BuildNetworks(g);

            return g;
        }

        /// <summary>Connected components of the account graph over links that count.</summary>
        private static void BuildNetworks(NetworkGraph g)
        {
            Dictionary<string, string> parent = g.Accounts.Keys.ToDictionary(a => a, a => a);

            string Find(string x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }

                return x;
            }

            foreach (AccountLink link in g.Links.Values.Where(l => l.IsLink))
            {
                if (!parent.ContainsKey(link.A)) parent[link.A] = link.A;
                if (!parent.ContainsKey(link.B)) parent[link.B] = link.B;

                string ra = Find(link.A), rb = Find(link.B);

                if (ra != rb)
                    parent[ra] = rb;
            }

            int id = 1;

            foreach (var group in parent.Keys.ToList().GroupBy(Find).OrderByDescending(gr => gr.Count()))
            {
                AccountNetwork n = new AccountNetwork { Id = id++ };

                n.Accounts.AddRange(group.OrderBy(a => a));
                g.Networks.Add(n);

                foreach (string account in n.Accounts)
                {
                    g.ByAccount[account] = n;
                    GetInfo(g, account);
                }
            }

            foreach (AccountLink link in g.Links.Values.Where(l => l.IsLink))
                g.ByAccount[link.A].Links.Add(link);
        }
    }
}
