﻿using Message;
using NamedPipeWrapper;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace MiningService
{
    internal static class Utilities
    {
        #region Public variables

        private static bool isSystem;

        //This version string is actually quite useless. I just use it to verify the running version in log files.
        public static string version = "0.2.1";

        #endregion Public variables

        #region DLLImports and enums (ThreadExecutionState, WTSQuerySession)

        internal static HardwareMonitor temperatureMonitor;
        [Flags]
        public enum EXECUTION_STATE : uint
        {
            ES_SYSTEM_REQUIRED = 0x00000001,
            ES_DISPLAY_REQUIRED = 0x00000002,
            ES_CONTINUOUS = 0x80000000
        }

        public enum WtsInfoClass
        {
            WTSInitialProgram,
            WTSApplicationName,
            WTSWorkingDirectory,
            WTSOEMId,
            WTSSessionId,
            WTSUserName,
            WTSWinStationName,
            WTSDomainName,
            WTSConnectState,
            WTSClientBuildNumber,
            WTSClientName,
            WTSClientDirectory,
            WTSClientProductId,
            WTSClientHardwareId,
            WTSClientAddress,
            WTSClientDisplay,
            WTSClientProtocolType,
            WTSIdleTime,
            WTSLogonTime,
            WTSIncomingBytes,
            WTSOutgoingBytes,
            WTSIncomingFrames,
            WTSOutgoingFrames,
            WTSClientInfo,
            WTSSessionInfo,
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern EXECUTION_STATE SetThreadExecutionState(
        EXECUTION_STATE flags);

        [DllImport("Wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr pointer);

        [DllImport("Wtsapi32.dll")]
        private static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, WtsInfoClass wtsInfoClass, out System.IntPtr ppBuffer, out int pBytesReturned);

        #endregion DLLImports and enums (ThreadExecutionState, WTSQuerySession)

        #region Check for network connection/MinerProxy server status

        public static bool CheckForInternetConnection()
        {
            //This is called to verify network connectivity, I personally use a MinerProxy instance's built-in web server API at /status, which returns "True".
            //In theory, anything that actually loads should work.

            if (!Config.settings.verifyNetworkConnectivity)
                return true;

            WebClient client = new WebClient();
            try
            {
                using (client.OpenRead(Config.settings.urlToCheckForNetwork))
                {
                }
                Log("Network Connectivity verified.");
                return true;
            }
            catch (WebException)
            {
                Log("Network Conectivity URL unreachable.");
                return false;
            }
        }

        #endregion Check for network connection/MinerProxy server status

        #region CPU utils

        //todo: Get CPU temperature function
        public static int GetCpuUsage()
        {
            // This returns, in a % of 100, the current CPU usage over a 1 second period.
            try
            {
                var cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                cpuCounter.NextValue();
                System.Threading.Thread.Sleep(1000);
                int cpuPercent = (int)cpuCounter.NextValue();

                //Add to the rolling average of CPU temps
                Config.AddCpuUsageQueue(cpuPercent);

                return cpuPercent;
            }
            catch (Exception ex)
            {
                //Debug("GetCpuUsage: " + ex.Message);
                return 50;
            }
        }

        #endregion CPU utils

        #region GPU utils

        //todo: Get GPU temperatures

        #endregion GPU utils

        #region Process utilities

        public static bool AreMinersRunning(List<MinerList> miners, bool isUserIdle, bool isCpuList)
        {
            bool areMinersRunning = true;
            int disabled = 0;

            //Debug("AreMinersRunning entered");

            foreach (var miner in miners)
            {
                if (isCpuList && Config.isCpuTempThrottled)
                    return true;

                if (!isCpuList && Config.isGpuTempThrottled)
                    return true;

                Process[] proc = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(miner.executable));

                if (miner.minerDisabled || (!miner.mineWhileNotIdle && !Config.isUserIdle))
                {
                    if (proc.Length > 0)
                        KillProcess(miner.executable);

                    disabled++;
                }
                else
                {
                    if (miner.isMiningIdleSpeed != isUserIdle && !miner.minerDisabled && (miner.idleArguments != miner.activeArguments))
                    {
                        Utilities.Debug("Miner " + miner.executable + " is not running in correct mode!");
                        KillProcess(miner.executable);
                        areMinersRunning = false;
                    }
                    else if (proc.Length == 0)
                    {
                        areMinersRunning = false;
                    }
                    else if (proc.Length > 0)
                    {
                        areMinersRunning = true;
                        miner.launchAttempts = 0;
                    }
                }
            }

            if (disabled == miners.Count)
                areMinersRunning = true;

            //Debug("AreMinersRunning exited. areMinersRunning: " + areMinersRunning + " " + disabled + " " + miners.Count);
            return areMinersRunning;
        }

        public static bool IsProcessRunning(string process)
        {
            Process[] proc = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(process));

            if (proc.Length == 0)
                return false;

            if (proc.Length > 1)
            {
                Utilities.Debug("More than one " + process);
                Utilities.KillProcess(process);
                return false;
            }

            return true;
        }

        public static bool IsProcessRunning(MinerList miner)
        {
            Process[] proc = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(miner.executable));

            if (proc.Length == 0)
                return false;

            return true;
        }

        public static void KillIdlemon(NamedPipeClient<IdleMessage> client)
        {
            //Debug("KillMiners entered");
            //loop through the CPU miner list and kill all miners
            if (IsProcessRunning(Config.idleMonExecutable) && Config.isPipeConnected)
            {
                client.PushMessage(new IdleMessage
                {
                    packetId = (int)Config.PacketID.Stop,
                    isIdle = false,
                    requestId = (int)Config.PacketID.None,
                    data = ""
                });

                System.Threading.Thread.Sleep(1000);

                if (IsProcessRunning(Config.idleMonExecutable))
                    KillProcess(Config.idleMonExecutable);
            }
        }

        public static void KillMinerList(List<MinerList> miners)
        {
            foreach (var miner in miners)
            {
                if (miner.shouldMinerBeRunning || IsProcessRunning(miner))
                {
                    Debug("Killing miner " + miner.executable);
                    KillProcess(Path.GetFileNameWithoutExtension(miner.executable));
                    miner.shouldMinerBeRunning = false;
                    miner.launchAttempts = 0;
                }
            }
        }

        private static void SetAfterburnerProfile(int profile)
        {
            if (profile == 0)
                return;

            if (!File.Exists(Config.settings.afterBurnerExePath))
                return;

            if (Config.currentAfterburnerProfile == profile)
                return;

            if (!Config.settings.autoSwitchMsiAfterburnerProfile)
                return;

            LaunchProcess(Config.settings.afterBurnerExePath, "-Profile" + profile);
            Config.currentAfterburnerProfile = profile;
        }

        public static void ChangeAfterburnerProfile()
        {
            Utilities.Log($"{Config.currentAfterburnerProfile} {Config.settings.afterBurnerExePath} {Config.settings.afterBurnerActiveProfile} {Config.settings.afterBurnerIdleProfile}");
            if (Config.isUserIdle)
                SetAfterburnerProfile(Config.settings.afterBurnerIdleProfile);
            else
                SetAfterburnerProfile(Config.settings.afterBurnerActiveProfile);
        }

        public static void KillMiners()
        {
            //Debug("KillMiners entered");
            //loop through the CPU miner list and kill all miners
            if (Config.settings.mineWithCpu)
            {
                foreach (var miner in Config.settings.cpuMiners)
                {
                    if (miner.shouldMinerBeRunning || IsProcessRunning(miner))
                    {
                        Debug("Killing miner " + miner.executable);
                        KillProcess(Path.GetFileNameWithoutExtension(miner.executable));
                        miner.shouldMinerBeRunning = false;
                        miner.launchAttempts = 0;
                    }
                }
            }

            //loop through the GPU miner list and kill all miners
            if (Config.settings.mineWithGpu)
            {
                foreach (var miner in Config.settings.gpuMiners)
                {
                    if (miner.shouldMinerBeRunning || IsProcessRunning(miner))
                    {
                        Debug("Killing miner " + miner.executable);
                        KillProcess(Path.GetFileNameWithoutExtension(miner.executable));
                        miner.shouldMinerBeRunning = false;
                        miner.launchAttempts = 0;
                    }
                }
            }

            //Debug("KillMiners exited");

            //we're no longer mining
            Config.isCurrentlyMining = false;
            Utilities.ChangeAfterburnerProfile();

        }

        public static bool KillProcess(string proc)
        {
            bool cantKillProcess = false;

            //Debug("KillProcess entered: " + proc);

            try
            {
                foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(proc)))
                {
                    Debug("Process found: " + p.Id + " - " + p.ProcessName);
                    if (!p.HasExited) p.Kill();

                    p.WaitForExit(2000);    //wait a max of 2 seconds for the process to terminate

                    if (!p.HasExited)
                        cantKillProcess = true;

                    Debug("Killed " + p.ProcessName + "(" + cantKillProcess + ")");
                }
            }
            catch (Exception ex)
            {
                Utilities.Log("KillProcess: " + ex.Message + '\n' + ex.Source);
                return false;
            }

            //Debug("KillProcess exited. cantKillProcess: " + cantKillProcess);

            //if we can't kill one of the processes, we should return FALSE!
            return !cantKillProcess;
        }

        public static bool LaunchMiners(List<MinerList> minerList)
        {
            bool launchIssues = false;
            bool isRunning = false;

            //Debug("LaunchMiners entered");

            foreach (var miner in minerList)
            {
                isRunning = IsProcessRunning(miner);
                Debug("shouldMinerBeRunning: (" + miner.executable + ") " + miner.shouldMinerBeRunning + " minerDisabled: " + miner.minerDisabled + " isRunning:" + isRunning + " isMiningIdleSpeed:" + miner.isMiningIdleSpeed + " launchAttempts: " + miner.launchAttempts);

                if ((miner.shouldMinerBeRunning && !miner.minerDisabled) &&
                    (!isRunning || (miner.isMiningIdleSpeed != Config.isUserIdle)) &&
                    miner.launchAttempts < 5 && !Config.isMiningPaused)
                {
                    if (LaunchProcess(miner) <= 0)  //returns PID
                    {
                        Utilities.Log("LaunchMiners: Unable to launch " + miner.executable + " " + (Config.isUserIdle ? miner.idleArguments : miner.activeArguments));
                        launchIssues = true;
                    }
                    miner.shouldMinerBeRunning = true;
                    miner.isMiningIdleSpeed = Config.isUserIdle;
                }
                else if (miner.shouldMinerBeRunning && isRunning && miner.launchAttempts <= 4)
                {
                    miner.launchAttempts = 0;
                }
                else if (miner.launchAttempts == 4 && !miner.minerDisabled)
                {
                    Log("Miner " + miner.executable + " has failed to launch 5 times, and is now disabled.");
                    miner.minerDisabled = true;
                }
            }

            //Debug("LaunchMiners exited. LaunchIssues: " + launchIssues);

            Config.isCurrentlyMining = true;
            Utilities.ChangeAfterburnerProfile();

            return !launchIssues;
        }

        public static int LaunchProcess(string exe, string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.RedirectStandardOutput = false;
            psi.RedirectStandardError = false;
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);

            Process proc = new Process();
            proc.StartInfo = psi;

            Debug("Starting Process " + exe + " " + args);

            try
            {
                proc = Process.Start(exe, args);
            }
            catch (Exception ex)
            {
                Log("LaunchProcess exe: " + ex.ToString());
            }
            return proc.Id;
        }

        //This one accepts a MinerList as the passed argument, and uses the Executable and Arguments of that particular miner.
        public static int LaunchProcess(MinerList miner)
        {
            if (miner.minerDisabled)
                return 0;

            miner.launchAttempts++;

            string arguments = Config.isUserIdle ? miner.idleArguments : miner.activeArguments;
            miner.isMiningIdleSpeed = Config.isUserIdle;
            Utilities.ChangeAfterburnerProfile();

            if (arguments.Length == 0)
                return 0;

            if (Config.settings.runInUserSession && Config.isPipeConnected)
            {
                Config.client.PushMessage(new IdleMessage
                {
                    packetId = (int)Config.PacketID.RunProgram,
                    isIdle = false,
                    requestId = (int)Config.PacketID.None,
                    data = miner.executable,
                    data2 = arguments
                });

                return 1;
            }
            else
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.RedirectStandardOutput = false;
                psi.RedirectStandardError = false;
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetDirectoryName(miner.executable);

                Process proc = new Process();
                proc.StartInfo = psi;

                Debug("Starting Process " + miner.executable + " " + arguments);

                miner.isMiningIdleSpeed = Config.isUserIdle;

                try
                {
                    proc = Process.Start(miner.executable, arguments);
                }
                catch (Exception ex)
                {
                    Log("launchProcess miner: " + ex.ToString());
                }

                return proc.Id;
            }
            return -1;
        }

        public static void MinersShouldBeRunning(List<MinerList> minerList)
        {
            foreach (var miner in minerList)
            {
                if ((!miner.minerDisabled && miner.launchAttempts < 4) && (miner.mineWhileNotIdle || Config.isUserIdle) && !Config.isMiningPaused)
                {
                    miner.shouldMinerBeRunning = true;
                }
                else
                {
                    miner.shouldMinerBeRunning = false;
                }
                miner.isMiningIdleSpeed = false;
            }
        }

        #endregion Process utilities

        #region System/OS Utils

        public static TimeSpan UpTime
        {
            get
            {
                try
                {
                    using (var uptime = new PerformanceCounter("System", "System Up Time"))
                    {
                        uptime.NextValue();       //Call this an extra time before reading its value
                        return TimeSpan.FromSeconds(uptime.NextValue());
                    }
                }
                catch (Exception ex)
                {
                    Debug("UpTime: " + ex.Message);
                    return TimeSpan.Zero;
                }
            }
        }

        public static void AllowSleep()
        {
            //this sets the ThreadExecutionState to allow the computer to sleep.
            SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
        }

        public static string CalculateMD5(string input)
        {
            MD5 md5 = System.Security.Cryptography.MD5.Create();

            byte[] inputBytes = System.Text.Encoding.ASCII.GetBytes(input);
            byte[] hash = md5.ComputeHash(inputBytes);

            StringBuilder sb = new StringBuilder();

            for (int i = 0; i < hash.Length; i++)
            {
                sb.Append(hash[i].ToString("X2"));
            }

            //Utilities.Log($"CalculateMD5: {sb.ToString()}");
            return sb.ToString();
        }

        public static void CheckForSystem(int sessionId)
        {
            //todo: use the new static Config class, and set these variables
            //This checks who is currently logged into the active Windows Session (think Desktop user)
            if (Utilities.GetUsernameBySessionId(sessionId, false) == "SYSTEM")
            {
                Debug("CheckForSystem: SYSTEM " + sessionId);
                KillMiners();
                KillProcess(Config.idleMonExecutable);
                Config.isUserLoggedIn = false;
                Config.isPipeConnected = false;
                Config.isUserIdle = true;
            }
            else
            {
                Config.isUserLoggedIn = true;
            }
        }

        public static string GenerateAuthString(string userName)
        {
            // Since this uses pipes, and should be connected on the same system, using the current
            // date as a sort of salt should work out fine. MD5 is fast enough that we could probably
            // use the current system seconds as well, but it's a small chance to fail, so we leave
            // that off.
            string date = DateTime.Now.ToString(@"yyyy\-MM\-dd HH\:mm");

            //Calculate the first auth string from GUID file:LSFMiningService:Current system date/time
            string auth = $"{ReadMachineGuid()}:LSFMiningService:{date}";

            //Calculate second auth string from first auth's MD5, plus machine name
            string auth2 = $"{CalculateMD5(auth)}:{Environment.MachineName}";

            //Calculate auth3 string from auth2's MD5, plus supplied username
            string auth3 = $"{CalculateMD5(auth2)}:{userName}";

            //Finally, calculate actual auth string with auth3's MD5
            string finalAuth = CalculateMD5(auth3);

            //Utilities.Log($"GenerateAuthString: Auth: {auth} \n Auth2: {auth2} \n Auth3: {auth3} \n finalAuth: {finalAuth}");

            return finalAuth;
        }

        public static string GetUsernameBySessionId(int sessionId, bool prependDomain = false)
        {
            //This returns the current logged in user, or if none is found, SYSTEM.
            IntPtr buffer;
            int strLen;
            string username = "SYSTEM"; //This may cause issues on other locales, so we may need to find a better method of detecting no logged in user.
            if (WTSQuerySessionInformation(IntPtr.Zero, sessionId, WtsInfoClass.WTSUserName, out buffer, out strLen) && strLen > 1)
            {
                username = Marshal.PtrToStringAnsi(buffer);
                WTSFreeMemory(buffer);
                if (prependDomain)
                {
                    if (WTSQuerySessionInformation(IntPtr.Zero, sessionId, WtsInfoClass.WTSDomainName, out buffer, out strLen) && strLen > 1)
                    {
                        username = Marshal.PtrToStringAnsi(buffer) + "\\" + username;
                        WTSFreeMemory(buffer);
                    }
                }
            }
            return username;
        }

        public static bool Is64BitOS()
        {
            //Returns true if the computer is 64bit
            return (System.Environment.Is64BitOperatingSystem);
        }

        public static bool IsSystem(bool cached = true)
        {
            //This is used to verify the service is running as an actual Service (Running as the SYSTEM user)

            if (cached)
                return isSystem;

            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                isSystem = identity.IsSystem;
            }

            return isSystem;
        }

        public static bool IsWinVistaOrHigher()
        {
            //This returns true if the OS version is higher than XP. XP machines generally speaking won't work well for mining.
            //Often times there will be .net issues as well, as we will be using a newer version of the .net framework
            OperatingSystem OS = Environment.OSVersion;
            return (OS.Platform == PlatformID.Win32NT) && (OS.Version.Major >= 6);
        }

        public static void PreventSleep()
        {
            //This sets the ThreadExecutionState to (attempt) to prevent the computer from sleeping
            //todo: This may need to be implemented into IdleMon instead of MiningService. Needs testing.
            SetThreadExecutionState(
              EXECUTION_STATE.ES_SYSTEM_REQUIRED |
              EXECUTION_STATE.ES_CONTINUOUS);
        }

        public static string ReadMachineGuid()
        {
            string id = "";

            try
            {
                id = File.ReadAllText(ApplicationPath() + "MachineID.txt");
            }
            catch (Exception ex) { Utilities.Log("ReadMachineGuid: " + ex.Message); }

            //Log("ReadMachineGuid: " + id);

            return id;
        }

        public static bool VerifyAuthString(string md5Hash, string userName)
        {
            bool success = GenerateAuthString(userName) == md5Hash;
            Utilities.Log($"VerifyAuthString: {success} - {md5Hash}");

            return success;
        }

        #endregion System/OS Utils

        #region Battery utils

        public static bool DoesBatteryExist()
        {
            System.Windows.Forms.PowerStatus pw = SystemInformation.PowerStatus;

            if (pw.BatteryChargeStatus == BatteryChargeStatus.NoSystemBattery)
                return false;

            return true;
        }

        public static bool IsBatteryFull()
        {
            System.Windows.Forms.PowerStatus pw = SystemInformation.PowerStatus;

            float floatBatteryPercent = 100 * SystemInformation.PowerStatus.BatteryLifePercent;
            int batteryPercent = (int)floatBatteryPercent;

            if (pw.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery))
                return true;

            if (batteryPercent == 100)
                return true;

            if (SystemInformation.PowerStatus.BatteryChargeStatus == BatteryChargeStatus.Charging || SystemInformation.PowerStatus.BatteryChargeStatus == BatteryChargeStatus.High)
                return true;

            return false;
        }

        #endregion Battery utils

        #region Logging

        public static void Debug(string text)
        {
            if (!IsSystem() && Config.settings.enableDebug)
                Console.WriteLine(DateTime.Now.ToString() + " DEBUG: " + text);

            try
            {
                if (Config.settings.enableLogging && Config.settings.enableDebug)
                    File.AppendAllText(ApplicationPath() + System.Environment.MachineName + ".txt", DateTime.Now.ToString() + " (" + Process.GetCurrentProcess().Id + ") DEBUG: " + text + System.Environment.NewLine);
            }
            catch
            {
            }
        }

        public static void Log(string text, bool force = false)
        {
            if (!IsSystem())
                Console.WriteLine(DateTime.Now.ToString() + " LOG: " + text);

            try
            {
                if (force || Config.settings.enableLogging)
                    File.AppendAllText(ApplicationPath() + System.Environment.MachineName + ".txt", DateTime.Now.ToString() + " (" + Process.GetCurrentProcess().Id + ") LOG: " + text + System.Environment.NewLine);
            }
            catch
            {
            }
        }

        #endregion Logging

        #region ApplicationPath

        private static string PathAddBackslash(string path)
        {
            if (path == null)
                throw new ArgumentNullException(nameof(path));

            path = path.TrimEnd();

            if (PathEndsWithDirectorySeparator())
                return path;

            return path + GetDirectorySeparatorUsedInPath();

            bool PathEndsWithDirectorySeparator()
            {
                char lastChar = path.Last();
                return lastChar == Path.DirectorySeparatorChar
                    || lastChar == Path.AltDirectorySeparatorChar;
            }

            char GetDirectorySeparatorUsedInPath()
            {
                if (path.Contains(Path.DirectorySeparatorChar))
                    return Path.DirectorySeparatorChar;

                return Path.AltDirectorySeparatorChar;
            }
        }

        public static string ApplicationPath()
        {
            return PathAddBackslash(AppDomain.CurrentDomain.BaseDirectory);
            //return System.Reflection.Assembly.GetExecutingAssembly().Location;
        }

        #endregion ApplicationPath
    }
}