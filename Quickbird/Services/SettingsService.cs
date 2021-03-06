﻿namespace Quickbird.Util
{
    using System;
    using System.ComponentModel;
    using System.Runtime.CompilerServices;
    using Windows.Foundation.Collections;
    using Windows.Storage;
    using JetBrains.Annotations;

    /// <summary>A singleton to create settings properties in. Local and roaming are hard coded into
    /// individual properties.</summary>
    internal class SettingsService : INotifyPropertyChanged
    {
        public delegate void ChangeHandler();

        private readonly ApplicationDataContainer _localSettings;
        private ApplicationDataCompositeValue _combinedCreds;
        private Guid _credStableSid;
        private string _credToken;
        private string _credUserId;
        private bool _isLoggedIn;

        /// <summary>Creata a new settings obbject that gives acces to local and roaming settings.</summary>
        private SettingsService()
        {
            _localSettings = ApplicationData.Current.LocalSettings;

            if (!_localSettings.Values.ContainsKey(nameof(CombinedCredentials)))
            {
                _combinedCreds = new ApplicationDataCompositeValue();
                _localSettings.Values[nameof(CombinedCredentials)] = _combinedCreds;
            }
            else
            {
                _combinedCreds = CombinedCredentials;
                UpdateCredPropsFromCombined();
            }
            _localSettings.Values.MapChanged += ValuesOnMapChanged;
        }

        /// <summary>The StableSid extracted from within the token.</summary>
        public Guid CredStableSid
        {
            get { return _credStableSid; }
            private set
            {
                if (value == CredStableSid) return;
                _credStableSid = value;
                _combinedCreds[nameof(CredStableSid)] = value;
                OnPropertyChanged();
            }
        }

        /// <summary>The credentials token setting.</summary>
        public string CredToken
        {
            get { return _credToken; }
            private set
            {
                if (value == CredToken) return;
                _credToken = value;
                _combinedCreds[nameof(CredToken)] = value;
                OnPropertyChanged();
            }
        }

        /// <summary>The credentials userID setting.</summary>
        public string CredUserId
        {
            get { return _credUserId; }
            private set
            {
                if (value == CredUserId) return;
                _credUserId = value;
                _combinedCreds[nameof(CredUserId)] = value;
                OnPropertyChanged();
            }
        }

        /// <summary>Singleton instance accessor.</summary>
        public static SettingsService Instance { get; } = new SettingsService();

        public bool IsLoggedIn
        {
            get { return _isLoggedIn; }
            private set
            {
                if (value == IsLoggedIn) return;
                _isLoggedIn = value;
                _combinedCreds[nameof(IsLoggedIn)] = value;
                OnPropertyChanged();
            }
        }

        /// <summary>The last time the db (excluding histories) was successfully updated from the internet.</summary>
        public DateTimeOffset LastSuccessfulGeneralDbGet
        {
            get { return Get(_localSettings, default(DateTimeOffset)); }

            set
            {
                if (value == LastSuccessfulGeneralDbGet) return;
                Set(_localSettings, value);
                OnPropertyChanged();
            }
        }

        /// <summary>The last time the db (excluding histories) was successfully pushed to the internet.</summary>
        public DateTimeOffset LastSuccessfulGeneralDbPost
        {
            get { return Get(_localSettings, default(DateTimeOffset)); }

            set
            {
                if (value == LastSuccessfulGeneralDbPost) return;
                Set(_localSettings, value);
                OnPropertyChanged();
            }
        }

        /// <summary>Local setting that allows the app to run local network for device management. Defaults to
        /// false.</summary>
        public bool LocalDeviceManagementEnabled
        {
            get { return Get(_localSettings, false); }
            set
            {
                if (value == LocalDeviceManagementEnabled) return;
                Set(_localSettings, value);
                OnPropertyChanged();
            }
        }


        /// <summary>Enbales / disables toasts. Defaults to 
        /// True.</summary>
        public bool ToastsEnabled
        {
            get { return Get(_localSettings, true); }
            set
            {
                if (value == ToastsEnabled) return;
                Set(_localSettings, value);
                OnPropertyChanged();
            }
        }

        /// <summary>Enbales / disables toasts. Defaults to 
        /// True.</summary>
        public bool DebugToastsEnabled
        {
            get { return Get(_localSettings, false); }
            set
            {
                if (value == DebugToastsEnabled) return;
                Set(_localSettings, value);
                OnPropertyChanged();
            }
        }

        /// <summary>Local setting that enables or disables a vitual sensor device, for demo / testing purposes
        /// false.</summary>
        public bool VirtualDeviceEnabled
        {
            get { return Get(_localSettings, false); }
            set
            {
                if (value == VirtualDeviceEnabled) return;
                Set(_localSettings, value);
                OnPropertyChanged();
            }
        }

        public Guid MachineID
        {
            get { //If no guid exists, create. Can;t set externally. 
                Guid id = Get(_localSettings, Guid.Empty);
                if (id == Guid.Empty)
                {
                    id = Guid.NewGuid(); 
                    Set(_localSettings, id);
                }

                return id; 
            }
        }

        private ApplicationDataCompositeValue CombinedCredentials
        {
            get { return Get(_localSettings, default(ApplicationDataCompositeValue)); }
            set
            {
                Set(_localSettings, value);
                UpdateCredPropsFromCombined();
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public event ChangeHandler CredsChanged;

        /// <summary>Method to unset a method value if it exists, otherwise it does nothing.</summary>
        /// <param name="settingsName">Name of the setting to unset.</param>
        /// <param name="settingsType">Roaming or local.</param>
        public void Delete([NotNull] string settingsName)
        {
            if (_localSettings.Values.ContainsKey(settingsName))
                _localSettings.Values.Remove(settingsName);
        }

        public void ResetDatabaseAndPostSettings()
        {
            LastSuccessfulGeneralDbGet = default(DateTimeOffset);
            LastSuccessfulGeneralDbPost = default(DateTimeOffset);
            VirtualDeviceEnabled = false;
            LocalDeviceManagementEnabled = false; 
        }

        public void SetNewCreds(string token, string userId, Guid stableSid)
        {
            CredToken = token;
            CredUserId = userId;
            CredStableSid = stableSid;
            IsLoggedIn = true;

            CombinedCredentials = _combinedCreds;
        }

        public void UnsetCreds()
        {
            IsLoggedIn = false;
            CredToken = null;
            CredUserId = null;
            CredStableSid = default(Guid);
        }

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>Gets the setting with the name of the property it is called from.</summary>
        /// <typeparam name="T">The type of the setting, must be the same as the property.</typeparam>
        /// <param name="settingsContainer">Local or roaming.</param>
        /// <param name="defaultValue">The value to return if the setting is not set.</param>
        /// <param name="settingsName">The name of the setting, should be autoset when called from a property.</param>
        /// <returns></returns>
        private T Get<T>([NotNull] ApplicationDataContainer settingsContainer, [CanBeNull] T defaultValue,
            [CallerMemberName] string settingsName = null)
        {
            if (settingsName == null)
            {
                throw new ArgumentException("Tried to get with a null settings value.");
            }
            if (settingsContainer.Values.ContainsKey(settingsName))
            {
                return (T) settingsContainer.Values[settingsName];
            }
            return defaultValue;
        }

        /// <summary>Sets the setting with the name of the property it is called from.</summary>
        /// <typeparam name="T">The type of the setting value.</typeparam>
        /// <param name="settingsContainer">Local or roaming.</param>
        /// <param name="value">The value to set.</param>
        /// <param name="settingsName">The name of the setting, should be autoset when called from a property.</param>
        private void Set<T>([NotNull] ApplicationDataContainer settingsContainer, T value,
            [CallerMemberName] string settingsName = null)
        {
            settingsContainer.Values[settingsName] = value;
        }

        private void UpdateCredPropsFromCombined()
        {
            var cc = CombinedCredentials;

            //This would trigger an infinite loop if the simple variable didn't check to see if the value is the same on setting.

            if (cc.ContainsKey(nameof(IsLoggedIn))) _isLoggedIn = (bool) cc[nameof(IsLoggedIn)];
            if (cc.ContainsKey(nameof(CredToken))) _credToken = (string) cc[nameof(CredToken)];
            if (cc.ContainsKey(nameof(CredUserId))) _credUserId = (string) cc[nameof(CredUserId)];
            if (cc.ContainsKey(nameof(CredStableSid))) _credStableSid = (Guid) cc[nameof(CredStableSid)];
        }

        private void ValuesOnMapChanged(IObservableMap<string, object> sender, IMapChangedEventArgs<string> @event)
        {
            if (sender.ContainsKey(nameof(CombinedCredentials)))
            {
                UpdateCredPropsFromCombined();
                _combinedCreds = CombinedCredentials;
                CredsChanged?.Invoke();
            }
        }
    }
}
