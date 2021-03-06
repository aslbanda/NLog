// 
// Copyright (c) 2004-2018 Jaroslaw Kowalski <jaak@jkowalski.net>, Kim Christensen, Julian Verdurmen
// 
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without 
// modification, are permitted provided that the following conditions 
// are met:
// 
// * Redistributions of source code must retain the above copyright notice, 
//   this list of conditions and the following disclaimer. 
// 
// * Redistributions in binary form must reproduce the above copyright notice,
//   this list of conditions and the following disclaimer in the documentation
//   and/or other materials provided with the distribution. 
// 
// * Neither the name of Jaroslaw Kowalski nor the names of its 
//   contributors may be used to endorse or promote products derived from this
//   software without specific prior written permission. 
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE 
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE 
// ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE 
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR 
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN 
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) 
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF 
// THE POSSIBILITY OF SUCH DAMAGE.
// 

#if !SILVERLIGHT && !__IOS__ && !__ANDROID__ && !NETSTANDARD

namespace NLog.LayoutRenderers
{
    using System;
    using System.Diagnostics;
    using System.Globalization;
    using System.Text;
    using NLog.Common;
    using NLog.Config;
    using NLog.Internal;

    /// <summary>
    /// The performance counter.
    /// </summary>
    [LayoutRenderer("performancecounter")]
    public class PerformanceCounterLayoutRenderer : LayoutRenderer, IRawValue
    {
        private PerformanceCounter _perfCounter;
        private CounterSample _prevSample = CounterSample.Empty;
        private CounterSample _nextSample = CounterSample.Empty;

        /// <summary>
        /// Gets or sets the name of the counter category.
        /// </summary>
        /// <docgen category='Performance Counter Options' order='10' />
        [RequiredParameter]
        public string Category { get; set; }

        /// <summary>
        /// Gets or sets the name of the performance counter.
        /// </summary>
        /// <docgen category='Performance Counter Options' order='10' />
        [RequiredParameter]
        public string Counter { get; set; }

        /// <summary>
        /// Gets or sets the name of the performance counter instance (e.g. this.Global_).
        /// </summary>
        /// <docgen category='Performance Counter Options' order='10' />
        public string Instance { get; set; }

        /// <summary>
        /// Gets or sets the name of the machine to read the performance counter from.
        /// </summary>
        /// <docgen category='Performance Counter Options' order='10' />
        public string MachineName { get; set; }

        /// <summary>
        /// Format string for conversion from float to string.
        /// </summary>
        /// <docgen category='Rendering Options' order='50' />
        public string Format { get; set; }

        /// <summary>
        /// Gets or sets the culture used for rendering. 
        /// </summary>
        /// <docgen category='Rendering Options' order='100' />
        public CultureInfo Culture { get; set; }

        /// <summary>
        /// Initializes the layout renderer.
        /// </summary>
        protected override void InitializeLayoutRenderer()
        {
            base.InitializeLayoutRenderer();

            _prevSample = CounterSample.Empty;
            _nextSample = CounterSample.Empty;

            if (MachineName != null)
            {
                _perfCounter = new PerformanceCounter(Category, Counter, Instance ?? string.Empty, MachineName);
            }
            else
            {
                string instance = Instance;
                if (string.IsNullOrEmpty(instance) && string.Equals(Category, "Process", StringComparison.OrdinalIgnoreCase))
                {
                    instance = GetCurrentProcessInstanceName(Category);
                }

                _perfCounter = new PerformanceCounter(Category, Counter, instance ?? string.Empty, true);
                GetValue(); // Prepare Performance Counter for CounterSample.Calculate
            }
        }

        /// <summary>
        /// If having multiple instances with the same process-name, then they will get different instance names
        /// </summary>
        private static string GetCurrentProcessInstanceName(string category)
        {
            try
            {
                using (Process proc = Process.GetCurrentProcess())
                {
                    int pid = proc.Id;
                    PerformanceCounterCategory cat = new PerformanceCounterCategory(category);
                    foreach (string instanceValue in cat.GetInstanceNames())
                    {
                        using (PerformanceCounter cnt = new PerformanceCounter(category, "ID Process", instanceValue, true))
                        {
                            int val = (int)cnt.RawValue;
                            if (val == pid)
                            {
                                return instanceValue;
                            }
                        }
                    }

                    InternalLogger.Debug("PerformanceCounter - Failed to auto detect current process instance. ProcessId={0}", pid);
                }
            }
            catch (Exception ex)
            {
                if (ex.MustBeRethrown())
                    throw;

                InternalLogger.Warn(ex, "PerformanceCounter - Failed to auto detect current process instance.");
            }
            return string.Empty;
        }

        /// <summary>
        /// Closes the layout renderer.
        /// </summary>
        protected override void CloseLayoutRenderer()
        {
            base.CloseLayoutRenderer();
            if (_perfCounter != null)
            {
                _perfCounter.Close();
                _perfCounter = null;
            }
            _prevSample = CounterSample.Empty;
            _nextSample = CounterSample.Empty;
        }

        /// <summary>
        /// Renders the specified environment variable and appends it to the specified <see cref="StringBuilder" />.
        /// </summary>
        /// <param name="builder">The <see cref="StringBuilder"/> to append the rendered data to.</param>
        /// <param name="logEvent">Logging event.</param>
        protected override void Append(StringBuilder builder, LogEventInfo logEvent)
        {
            var formatProvider = GetFormatProvider(logEvent, Culture);
            builder.Append(GetValue().ToString(Format, formatProvider));
        }

        /// <inheritdoc />
        object IRawValue.GetRawValue(LogEventInfo logEvent)
        {
            return GetValue();
        }

        private float GetValue()
        {
            CounterSample currentSample = _perfCounter.NextSample();
            if (currentSample.SystemFrequency != 0)
            {
                // The recommended delay time between calls to the NextSample method is one second, to allow the counter to perform the next incremental read.
                float timeDifferenceSecs = (currentSample.TimeStamp - _nextSample.TimeStamp) / (float)currentSample.SystemFrequency;
                if (timeDifferenceSecs > 0.5F || timeDifferenceSecs < -0.5F)
                {
                    _prevSample = _nextSample;
                    _nextSample = currentSample;
                    if (_prevSample.Equals(CounterSample.Empty))
                        _prevSample = currentSample;
                }
            }
            else
            {
                _prevSample = _nextSample;
                _nextSample = currentSample;
            }
            float sampleValue = CounterSample.Calculate(_prevSample, currentSample);
            return sampleValue;
        }
    }
}

#endif
