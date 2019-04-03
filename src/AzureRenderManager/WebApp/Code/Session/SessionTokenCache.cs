﻿using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using System.Security.Claims;
using System.Text;
using WebApp.Code.Extensions;

namespace WebApp.Code.Session
{
    public class SessionTokenCache
    {
        private static readonly object FileLock = new object();
        private readonly string _cacheKey;
        private ClaimsPrincipal _claimsPrincipal;
        private readonly IMemoryCache _memoryCache;
        private TokenCache _cache = new TokenCache();

        public SessionTokenCache(ClaimsPrincipal claimsPrincipal, IMemoryCache memoryCache)
        {
            _cacheKey = BuildCacheKey(claimsPrincipal);
            _claimsPrincipal = claimsPrincipal;
            _memoryCache = memoryCache;
            Load();
        }

        private static string BuildCacheKey(ClaimsPrincipal claimsPrincipal)
        {
            // TODO: This needs to be updated in a multi-tenant env.
            return $"UserId:{claimsPrincipal.Claims.GetObjectId()}";
        }

        public TokenCache GetCacheInstance()
        {
            _cache.BeforeAccess = BeforeAccessNotification;
            _cache.AfterAccess = AfterAccessNotification;
            Load();
            return _cache;
        }

        public void SaveUserStateValue(string state)
        {
            lock (FileLock)
            {
                _memoryCache.Set(_cacheKey + "_state", Encoding.ASCII.GetBytes(state));
            }
        }

        public string ReadUserStateValue()
        {
            string state;
            lock (FileLock)
            {
                state = Encoding.ASCII.GetString(_memoryCache.Get(_cacheKey + "_state") as byte[]);
            }

            return state;
        }

        public void Load()
        {
            lock (FileLock)
            {
                _cache.Deserialize(_memoryCache.Get(_cacheKey) as byte[]);
            }
        }

        public void Persist()
        {
            lock (FileLock)
            {
                // reflect changes in the persistent store
                _memoryCache.Set(_cacheKey, _cache.Serialize());
                // once the write operation took place, restore the HasStateChanged bit to false
                _cache.HasStateChanged = false;
            }
        }

        // Empties the persistent store.
        public void Clear()
        {
            _cache = null;
            lock (FileLock)
            {
                _memoryCache.Remove(_cacheKey);
            }
        }

        // Triggered right before MSAL needs to access the cache.
        // Reload the cache from the persistent store in case it changed since the last access.
        private void BeforeAccessNotification(TokenCacheNotificationArgs args)
        {
            Load();
        }

        // Triggered right after MSAL accessed the cache.
        private void AfterAccessNotification(TokenCacheNotificationArgs args)
        {
            // if the access operation resulted in a cache update
            if (_cache.HasStateChanged)
            {
                Persist();
            }
        }
    }
}