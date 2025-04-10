﻿//******************************************************************************************************
//  App.xaml.cs - Gbtc
//
//  Copyright © 2010, Grid Protection Alliance.  All Rights Reserved.
//
//  Licensed to the Grid Protection Alliance (GPA) under one or more contributor license agreements. See
//  the NOTICE file distributed with this work for additional information regarding copyright ownership.
//  The GPA licenses this file to you under the MIT License (MIT), the "License"; you may
//  not use this file except in compliance with the License. You may obtain a copy of the License at:
//
//      http://opensource.org/licenses/MIT
//
//  Unless agreed to in writing, the subject software distributed under the License is distributed on an
//  "AS-IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. Refer to the
//  License for the specific language governing permissions and limitations.
//
//  Code Modification History:
//  ----------------------------------------------------------------------------------------------------
//  08/22/2011 - Mehulbhai P Thakkar
//       Generated original version of source code.
//
//******************************************************************************************************

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Markup;
using System.Xml.Linq;
using GSF;
using GSF.Configuration;
using GSF.Data;
using GSF.Diagnostics;
using GSF.IO;
using GSF.Reflection;
using GSF.TimeSeries;
using GSF.TimeSeries.UI;
using GSF.Windows.ErrorManagement;
using openPDCManager.Properties;
using Application = System.Windows.Application;

// ReSharper disable RedundantExtendsListEntry
// ReSharper disable ConstantConditionalAccessQualifier
namespace openPDCManager
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        #region [ Members ]

        // Constants
        private const string SystemSettings = CommonFunctions.DefaultSettingsCategory;

        // Fields
        private Guid m_nodeID;
        private readonly Func<string> m_defaultErrorText;

        #endregion

        #region [ Constructors ]

        /// <summary>
        /// Creates an instance of <see cref="App"/> class.
        /// </summary>
        public App()
        {
            string systemName = null;

            AppDomain.CurrentDomain.SetPrincipalPolicy(PrincipalPolicy.WindowsPrincipal);
            Current.Startup += Application_Startup;
            Current.SessionEnding += Application_SessionEnding;

            try
            {
                string getElementValue(XElement[] elements, string name)
                {
                    XElement element = elements.Elements("add").FirstOrDefault(elem =>
                        elem.Attributes("name").Any(nameAttribute =>
                        string.Compare(nameAttribute.Value, name, StringComparison.OrdinalIgnoreCase) == 0));

                    return element?.Attributes("value").FirstOrDefault()?.Value;
                }

                string configPath = FilePath.GetAbsolutePath(ConfigurationFile.Current.Configuration.FilePath.Replace("Manager", ""));

                if (File.Exists(configPath))
                {
                    XDocument hostConfig = XDocument.Load(configPath);
                    XElement[] systemSettings = hostConfig.Descendants(SystemSettings).ToArray();

                    // Get system name from host service config
                    systemName = getElementValue(systemSettings, "SystemName");

                    // Validate manager database connection as compared to host service
                    string hostConnectionString = getElementValue(systemSettings, "ConnectionString");
                    string hostDataProviderString = getElementValue(systemSettings, "DataProviderString");
                    string hostNodeID = getElementValue(systemSettings, "NodeID");

                    ConfigurationFile config = ConfigurationFile.Current;
                    CategorizedSettingsElementCollection configSettings = config.Settings[SystemSettings];

                    configSettings.Add("KeepCustomConnection", "false", "Determines if manager should keep custom database connection setting separate from host service.");

                    if (!configSettings["KeepCustomConnection"].Value.ParseBoolean())
                    {
                        MethodInfo getRawValueMethod = typeof(CategorizedSettingsElement).GetMethod("GetRawValue", BindingFlags.Instance | BindingFlags.NonPublic);

                        string getRawValue(CategorizedSettingsElement element)
                        {
                            if (getRawValueMethod is null)
                                return element.Value;

                            return getRawValueMethod.Invoke(element, Array.Empty<object>()) as string;
                        }

                        // Get value from connection string that is still encrypted for proper comparison
                        string connectionString = getRawValue(configSettings["ConnectionString"]);
                        string dataProviderString = configSettings["DataProviderString"].Value;
                        string nodeID = configSettings["NodeID"].Value;

                        if (!hostConnectionString.Equals(connectionString, StringComparison.OrdinalIgnoreCase) ||
                            !hostDataProviderString.Equals(dataProviderString, StringComparison.OrdinalIgnoreCase) ||
                            !hostNodeID.Equals(nodeID, StringComparison.OrdinalIgnoreCase))
                        {
                            bool mismatchHandled = false;

                            if (Environment.CommandLine.Contains("-elevated") && Environment.CommandLine.Contains("-connection"))
                            {
                                if (Environment.CommandLine.Contains("-connectionFix"))
                                {
                                    configSettings["ConnectionString"].Value = hostConnectionString;
                                    configSettings["DataProviderString"].Value = hostDataProviderString;
                                    configSettings["NodeID"].Value = hostNodeID;
                                    config.Save();
                                    mismatchHandled = true;
                                }
                                else if (Environment.CommandLine.Contains("-connectionKeep"))
                                {
                                    configSettings["KeepCustomConnection"].Value = "true";
                                    config.Save();
                                    mismatchHandled = true;
                                }
                            }
                            
                            if (!mismatchHandled)
                            {
                                bool correctMismatch = System.Windows.Forms.MessageBox.Show(new NativeWindow(), "Manager database connection does not match host service. Do you want to correct this?", "Database Mismatch", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                                string elevatedOperation = correctMismatch ? "-connectionFix" : "-connectionKeep";

                                ProcessStartInfo startInfo = new ProcessStartInfo
                                {
                                    FileName = Environment.GetCommandLineArgs()[0],
                                    Arguments = $"{string.Join(" ", Environment.GetCommandLineArgs().Skip(1))} -elevated {elevatedOperation}",
                                    UseShellExecute = true,
                                    Verb = "runas"
                                };

                                using (Process.Start(startInfo)) { }
                                Environment.Exit(0);
                            }
                        }
                    }
                }
            }
            catch
            {
                // If this fails, it's not a huge deal
            }

            ErrorLogger = new ErrorLogger
            {
                ExitOnUnhandledException = false, 
                HandleUnhandledException = true, 
                LogToEmail = false, 
                LogToEventLog = true, 
                LogToFile = true, 
                LogToScreenshot = true, 
                LogToUI = true
            };

            m_defaultErrorText = ErrorLogger.ErrorTextMethod;
            ErrorLogger.ErrorTextMethod = ErrorText;

            ErrorLogger.Initialize();

            Title = AssemblyInfo.EntryAssembly.Title;

            // Add system name to title
            if (!string.IsNullOrWhiteSpace(systemName))
                Title = $"{Title} [{systemName.Trim()}]";

            // Setup default cache for measurement keys and associated Guid based signal ID's
            AdoDataConnection database = null;

            try
            {
                database = new AdoDataConnection(SystemSettings);

                if (!Environment.CommandLine.Contains("-elevated"))
                {
                    ConfigurationFile configurationFile = ConfigurationFile.Current;
                    CategorizedSettingsElementCollection systemSettings = configurationFile.Settings[SystemSettings];
                    string elevateSetting = systemSettings["ElevateProcess"]?.Value;

                    bool elevateProcess = !string.IsNullOrEmpty(elevateSetting) ? elevateSetting.ParseBoolean() : database.IsSqlite;

                    if (elevateProcess)
                    {
                        ProcessStartInfo startInfo = new ProcessStartInfo
                        {
                            FileName = Environment.GetCommandLineArgs()[0],
                            Arguments = $"{string.Join(" ", Environment.GetCommandLineArgs().Skip(1))} -elevated",
                            UseShellExecute = true,
                            Verb = "runas"
                        };

                        using (Process.Start(startInfo)) { }
                        Environment.Exit(0);
                    }
                }

                MeasurementKey.EstablishDefaultCache(database.Connection, database.AdapterType);
            }
            catch (Exception ex)
            {
                LoadException = new InvalidOperationException($"{Title} cannot connect to database: {ex.Message}", ex);
            }
            finally
            {
                database?.Dispose();
            }

            IsolatedStorageManager.WriteToIsolatedStorage("MirrorMode", false);
        }

        #endregion

        #region [ Properties ]

        /// <summary>
        /// Gets or sets the ID of the node user is currently connected to.
        /// </summary>
        public Guid NodeID
        {
            get => m_nodeID;
            set
            {
                m_nodeID = value;
                m_nodeID.SetAsCurrentNodeID();
            }
        }

        /// <summary>
        /// Gets title of the application window.
        /// </summary>
        public string Title { get; }

        /// <summary>
        /// Gets the <see cref="ErrorLogger"/> for the application.
        /// </summary>
        public ErrorLogger ErrorLogger { get; }

        /// <summary>
        /// Gets the <see cref="Exception"/> that was thrown during the load process.
        /// </summary>
        public Exception LoadException { get; private set; }

        #endregion

        #region [ Methods ]

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            try
            {
                XmlLanguage xmlLanguage = XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.Name);
                FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(xmlLanguage));
            }
            catch (Exception ex)
            {
                Logger.SwallowException(ex);
            }
        }

        private static void Application_SessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            try
            {
                Settings.Default.Save();
            }
            catch (Exception ex)
            {
                Logger.SwallowException(ex);
            }
        }

        private string ErrorText()
        {
            string errorMessage;

            if (LoadException is not null)
            {
                errorMessage = LoadException.Message;
                LoadException = null;
                return errorMessage;
            }

            errorMessage = m_defaultErrorText();
            Exception ex = ErrorLogger.LastException;

            if (ex is null)
                return errorMessage;

            if (string.Compare(ex.Message, "UnhandledException", StringComparison.OrdinalIgnoreCase) == 0 && ex.InnerException is not null)
                ex = ex.InnerException;

            errorMessage = $"{errorMessage}\r\n\r\nError details: {ex.Message}";

            return errorMessage;
        }

        #endregion
    }
}
