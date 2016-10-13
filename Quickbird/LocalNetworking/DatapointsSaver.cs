﻿namespace Quickbird.LocalNetworking
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading.Tasks;
    using Windows.UI.Core;
    using Windows.UI.Xaml;
    using Data;
    using Qb.Poco.User;
    using Internet;
    using Microsoft.EntityFrameworkCore;
    using Models;
    using Qb.Poco.Global;
    using Util;

    /// <summary>Non-threadsafe singleton manager of the saving of datapoints.</summary>
    public class DatapointsSaver : IDisposable
    {
        private const int SaveIntervalSeconds = 60;

        private static DatapointsSaver _instance;
        // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
        /// <summary>Action for BroadcastMessaged, cannot be inlined due to weak ref use.</summary>
        private readonly Action<string> _onHardwareChanged;

        private readonly List<SensorBuffer> _sensorBuffer = new List<SensorBuffer>();
        private List<Device> _dbDevices;

        /// <summary>Most operations replace this task with a continuation on it to make the tasks sequential.</summary>
        private Task _localTask;

        private DispatcherTimer _saveTimer;
        private List<SensorType> _sensorTypes;

        public DatapointsSaver()
        {
            if (_instance == null)
            {
                _instance = this;

                Task.Run(() => ((App) Application.Current).Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    _saveTimer = new DispatcherTimer {Interval = TimeSpan.FromSeconds(SaveIntervalSeconds)};
                    _saveTimer.Tick += SaveBufferedReadings;
                    _saveTimer.Start();
                }));

                _onHardwareChanged = HardwareChanged;
                Messenger.Instance.TablesChanged.Subscribe(_onHardwareChanged);

                _localTask = Task.Run(() => { LoadData(); });
            }
            else
            {
                throw new Exception("You can't initialise more than one datapoint Saver");
            }
        }

        /// <summary>The date with hours, minutes and seconds set to zero</summary>
        public static DateTimeOffset Today
        {
            get
            {
                var today = DateTimeOffset.Now;
                return today.Subtract(today.TimeOfDay);
            }
        }

        public static DateTimeOffset Tomorrow
        {
            get
            {
                var tomorrow = DateTimeOffset.Now.AddDays(1);
                return tomorrow.Subtract(tomorrow.TimeOfDay);
            }
        }

        /// <summary>The date of yesturday with hours, minutes and seconds set to zero</summary>
        public static DateTimeOffset Yesturday
        {
            get
            {
                var yesturday = DateTimeOffset.Now.AddDays(-1);
                return yesturday.Subtract(yesturday.TimeOfDay);
            }
        }

        public void Dispose()
        {
            BlockingDispatcher.Run(() => _saveTimer?.Stop());
            _instance = null;
        }

        public void BufferAndSendReadings(KeyValuePair<Guid, Manager.SensorMessage[]> values)
        {
            //Purposefull fire and forget
            _localTask = _localTask.ContinueWith(previous =>
            {
                var device = _dbDevices.FirstOrDefault(dv => dv.SerialNumber == values.Key);

                if (device == null)
                {
                    CreateDevice(values);
                }
                else
                {
                    var sensorReadings = new List<Messenger.SensorReading>();
                    foreach (var message in values.Value)
                    {
                        try
                        {
                            var sensorBuffer = _sensorBuffer.First(sb => sb.Sensor.SensorTypeId == message.SensorTypeID);
                            var duration = TimeSpan.FromMilliseconds((double) message.duration/1000);
                            var timeStamp = DateTimeOffset.Now;
                            var datapoint = new SensorDatapoint(message.value, timeStamp, duration);
                            sensorBuffer.FreshBuffer.Add(datapoint);
                            var sensorReading = new Messenger.SensorReading(sensorBuffer.Sensor.Id, datapoint.Value,
                                datapoint.Timestamp, datapoint.Duration);
                            sensorReadings.Add(sensorReading);
                        }
                        catch (ArgumentNullException)
                        {
                            //TODO add a new sensor to the device!
                        }
                    }


                    //this is meant to be fire-forget, that's cool
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    WebSocketConnection.Instance.SendAsync(sensorReadings);
                    Messenger.Instance.NewSensorDataPoint.Invoke(sensorReadings);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                }
            });
        }

        public void Resume()
        {
            Debug.WriteLine("resuming datasaver");
            BlockingDispatcher.Run(() => _saveTimer?.Start());
        }


        public void Suspend()
        {
            Debug.WriteLine("suspending datasaver");
            BlockingDispatcher.Run(() => _saveTimer?.Stop());
        }

        /// <summary>To be used internaly</summary>
        /// <param name="values"></param>
        private bool CreateDevice(KeyValuePair<Guid, Manager.SensorMessage[]> values)
        {
            //Make sure that if fired several times, the constraints are maintained

            if (Settings.Instance.IsLoggedIn && Settings.Instance.LastSuccessfulGeneralDbGet != default(DateTimeOffset) &&
                _dbDevices.Any(dev => dev.SerialNumber == values.Key) == false)
            {
                Debug.WriteLine("addingDevice");

                var device = new Device
                {
                    Id = Guid.NewGuid(),
                    SerialNumber = values.Key,
                    Deleted = false,
                    CreatedAt = DateTimeOffset.Now,
                    UpdatedAt = DateTimeOffset.Now,
                    Name = string.Format("Box Number {0}", _dbDevices.Count),
                    Sensors = new List<Sensor>(),
                    Location =
                        new Location
                        {
                            Id = Guid.NewGuid(),
                            Deleted = false,
                            Name = string.Format("Box Number {0}", _dbDevices.Count),
                            PersonId = Settings.Instance.CredStableSid, //TODO use the thing from settings!
                            CropCycles = new List<CropCycle>(),
                            Devices = new List<Device>(),
                            CreatedAt = DateTimeOffset.Now,
                            UpdatedAt = DateTimeOffset.Now
                        }
                };

                //Add sensors
                foreach (var inSensors in values.Value)
                {
                    //Todo check correctness of hte sensorType
                    var newSensor = new Sensor
                    {
                        CreatedAt = DateTimeOffset.Now,
                        UpdatedAt = DateTimeOffset.Now,
                        Id = Guid.NewGuid(),
                        DeviceId = device.Id,
                        Deleted = false,
                        SensorTypeId = inSensors.SensorTypeID,
                        Enabled = true,
                    };
                    device.Sensors.Add(newSensor);
                }

                Local.AddDeviceWithItsLocationAndSensors(device);

                //Add the device to the cached data?
                _dbDevices.Add(device);
                foreach (var sensor in device.Sensors)
                {
                    _sensorBuffer.Add(new SensorBuffer(sensor));
                }

                return true;
            }
            return false;
        }

        private void HardwareChanged(string value)
        {
            _localTask = _localTask.ContinueWith(previous => { LoadData(); });
        }


        //TODO register this with an event in messenger class
        /// <summary>Loads device and sensor info from the DB.</summary>
        /// <remarks>Don't make publish or call directly! always push onto the task!</remarks>
        /// <returns>True if it loaded something, false otherwise.</returns>
        private void LoadData()
        {
            Debug.WriteLine("DatapointsSaver refreshing cache");

            _dbDevices = Local.GetDevicesWithSensors();
            var sensorsHistory = Local.GetTodaysSensorHistories();
            _sensorTypes = Local.GetSensorTypesWithParametersPlacementsAndSubsystems();

            //Add missing sensors and relays
            foreach (var sensor in _dbDevices.SelectMany(dv => dv.Sensors))
            {
                if (_sensorBuffer.Any(sb => sb.Sensor.Id == sensor.Id) == false)
                {
                    _sensorBuffer.Add(new SensorBuffer(sensor));
                }
            } //TODO merge the datapoints!
            foreach (var sHistory in sensorsHistory)
            {
                var data = SensorDatapoint.Deserialise(sHistory.RawData);
                var mIndex = _sensorBuffer.FindIndex(sb => sb.Sensor.Id == sHistory.SensorId);
                if (_sensorBuffer[mIndex].DataDay == null ||
                    _sensorBuffer[mIndex].DataDay.UtcDate < sHistory.UtcDate)
                {
                    _sensorBuffer[mIndex] = new SensorBuffer(_sensorBuffer[mIndex].Sensor, sHistory);
                }
                else if (_sensorBuffer[mIndex].DataDay.Data != null && sHistory.Data != null)
                {
                    var sHistMerged = SensorHistory.Merge(_sensorBuffer[mIndex].DataDay, sHistory);
                    _sensorBuffer[mIndex] = new SensorBuffer(_sensorBuffer[mIndex].Sensor, sHistMerged);
                }
            }
        }


        //Closes sensor Histories that are no longer usefull
        private void SaveBufferedReadings(object sender, object e)
        {
            Toast.Debug("SaveBufferedReadings", $"{DateTimeOffset.Now.DateTime} Datapointsaver");
            _localTask = _localTask.ContinueWith(previous =>
            {
                //if (Settings.Instance.LastSuccessfulGeneralDbGet > DateTimeOffset.Now - TimeSpan.FromMinutes(5))
                //{
                Debug.WriteLine("Datasaver started, did not bother detecting a recent update.");

                using (var db = new MainDbContext())
                {
                    for (var i = 0; i < _sensorBuffer.Count; i++)
                    {
                        var sbuffer = _sensorBuffer[i];

                        SensorDatapoint sensorDatapoint = null;

                        if (sbuffer.FreshBuffer.Count > 0)
                        {
                            var startTime = sbuffer.FreshBuffer[0].TimeStamp;
                            var endTime = sbuffer.FreshBuffer.Last().TimeStamp;
                            var duration = (endTime - startTime).Subtract(sbuffer.FreshBuffer[0].Duration);

                            var cumulativeDuration = TimeSpan.Zero;
                            double cumulativeValue = 0;

                            for (var b = 0; b < sbuffer.FreshBuffer.Count; b++)
                            {
                                cumulativeDuration += sbuffer.FreshBuffer[b].Duration;
                                cumulativeValue += sbuffer.FreshBuffer[b].Value;
                            }

                            var sensorType = _sensorTypes.First(st => st.Id == sbuffer.Sensor.SensorTypeId);
                            var value = cumulativeValue/sbuffer.FreshBuffer.Count;

                            if (sensorType.ParameterId == 5) // Level
                            {
                                sensorDatapoint = new SensorDatapoint(Math.Round(value), endTime, duration);
                            }
                            else if (sensorType.ParameterId == 9) //water flow
                            {
                                sensorDatapoint = new SensorDatapoint(value, endTime, cumulativeDuration);
                            }
                            else
                            {
                                sensorDatapoint = new SensorDatapoint(value, endTime, duration);
                            }

                            sbuffer.FreshBuffer.RemoveRange(0, sbuffer.FreshBuffer.Count);
                        }
                        //only if new data is present
                        if (sensorDatapoint != null)
                        {
                            //check if corresponding dataDay is too old or none exists at all
                            if (sbuffer.DataDay?.TimeStamp < sensorDatapoint.TimeStamp || sbuffer.DataDay == null)
                            {
                                var dataDay = new SensorHistory
                                {
                                    LocationId = sbuffer.Sensor.Device.LocationId,
                                    SensorId = sbuffer.Sensor.Id,
                                    Sensor = sbuffer.Sensor,
                                    TimeStamp = Tomorrow,
                                    Data = new List<SensorDatapoint>()
                                };
                                _sensorBuffer[i] = new SensorBuffer(sbuffer.Sensor, dataDay);
                                //Only uses this entity, and does not follow the references to stick related references in the DB
                                db.Entry(dataDay).State = EntityState.Added;
                            }
                            else
                            {
                                //this will not attach related entities, which is good
                                db.Entry(sbuffer.DataDay).State = EntityState.Unchanged;
                            }

                            _sensorBuffer[i].DataDay.Data.Add(sensorDatapoint);
                            _sensorBuffer[i].DataDay.SerialiseData();
                        }
                    } //for loop ends
                    //Once we are done here, mark changes to the db
                    db.SaveChanges();
                    Debug.WriteLine("Saved Sensor Data");
                }
                //}
                //else
                //{
                //    Debug.WriteLine("Skipped datasaver due to lack of recent update.");
                //}
            });
        }


        private class SensorBuffer
        {
            public readonly SensorHistory DataDay;
            public readonly List<SensorDatapoint> FreshBuffer;
            public readonly Sensor Sensor;

            public SensorBuffer(Sensor assignSensor, SensorHistory inDataDay = null)
            {
                Sensor = assignSensor;
                FreshBuffer = new List<SensorDatapoint>();
                DataDay = inDataDay;
            }
        }
    }
}
