﻿using Newtonsoft.Json;
using NiceHashMiner.Configs;
using NiceHashMiner.Interfaces;
using NVIDIA.NVAPI;
using ManagedCuda.Nvml;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Windows.Forms;
using NiceHashMiner.Devices.Querying;
using NiceHashMinerLegacy.Common.Enums;
using static NiceHashMiner.Translations;
using NiceHashMiner.Devices.OpenCL;
using NiceHashMiner.PInvoke;

namespace NiceHashMiner.Devices
{
    /// <summary>
    /// ComputeDeviceManager class is used to query ComputeDevices avaliable on the system.
    /// Query CPUs, GPUs [Nvidia, AMD]
    /// </summary>
    public class ComputeDeviceManager
    {
        public static class Query
        {
            private const string Tag = "ComputeDeviceManager.Query";

            // format 372.54;
            private class NvidiaSmiDriver
            {
                public NvidiaSmiDriver(int left, int right)
                {
                    LeftPart = left;
                    _rightPart = right;
                }

                public bool IsLesserVersionThan(NvidiaSmiDriver b)
                {
                    if (LeftPart < b.LeftPart)
                    {
                        return true;
                    }
                    return LeftPart == b.LeftPart && GetRightVal(_rightPart) < GetRightVal(b._rightPart);
                }

                public override string ToString()
                {
                    return $"{LeftPart}.{_rightPart}";
                }

                public readonly int LeftPart;
                private readonly int _rightPart;

                private static int GetRightVal(int val)
                {
                    if (val >= 10)
                    {
                        return val;
                    }
                    return val * 10;
                }
            }

            private static readonly NvidiaSmiDriver NvidiaRecomendedDriver = new NvidiaSmiDriver(372, 54); // 372.54;
            private static readonly NvidiaSmiDriver NvidiaMinDetectionDriver = new NvidiaSmiDriver(362, 61); // 362.61;
            private static NvidiaSmiDriver _currentNvidiaSmiDriver = new NvidiaSmiDriver(-1, -1);
            private static readonly NvidiaSmiDriver InvalidSmiDriver = new NvidiaSmiDriver(-1, -1);

            // naming purposes
            public static int CpuCount = 0;

            public static int GpuCount = 0;

            private static NvidiaSmiDriver GetNvidiaSmiDriver()
            {
                if (WindowsDisplayAdapters.HasNvidiaVideoController())
                {
                    string stdErr;
                    string args;
                    var stdOut = stdErr = args = string.Empty;
                    var smiPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) +
                                  "\\NVIDIA Corporation\\NVSMI\\nvidia-smi.exe";
                    if (smiPath.Contains(" (x86)")) smiPath = smiPath.Replace(" (x86)", "");
                    try
                    {
                        var P = new Process
                        {
                            StartInfo =
                            {
                                FileName = smiPath,
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                CreateNoWindow = true
                            }
                        };
                        P.Start();
                        P.WaitForExit(15 * 1000);

                        stdOut = P.StandardOutput.ReadToEnd();
                        stdErr = P.StandardError.ReadToEnd();

                        const string findString = "Driver Version: ";
                        using (var reader = new StringReader(stdOut))
                        {
                            var line = string.Empty;
                            do
                            {
                                line = reader.ReadLine();
                                if (line != null && line.Contains(findString))
                                {
                                    var start = line.IndexOf(findString);
                                    var driverVer = line.Substring(start, start + 7);
                                    driverVer = driverVer.Replace(findString, "").Substring(0, 7).Trim();
                                    var drVerDouble = double.Parse(driverVer, CultureInfo.InvariantCulture);
                                    var dot = driverVer.IndexOf(".");
                                    var leftPart = int.Parse(driverVer.Substring(0, 3));
                                    var rightPart = int.Parse(driverVer.Substring(4, 2));
                                    return new NvidiaSmiDriver(leftPart, rightPart);
                                }
                            } while (line != null);
                        }
                    }
                    catch (Exception ex)
                    {
                        Helpers.ConsolePrint(Tag, "GetNvidiaSMIDriver Exception: " + ex.Message);
                        return InvalidSmiDriver;
                    }
                }
                return InvalidSmiDriver;
            }

            private static void ShowMessageAndStep(string infoMsg)
            {
                MessageNotifier?.SetMessageAndIncrementStep(infoMsg);
            }

            public static IMessageNotifier MessageNotifier { get; private set; }

            public static bool CheckVideoControllersCountMismath()
            {
                // this function checks if count of CUDA devices is same as it was on application start, reason for that is
                // because of some reason (especially when algo switching occure) CUDA devices are dissapiring from system
                // creating tons of problems e.g. miners stop mining, lower rig hashrate etc.

                /* commented because when GPU is "lost" windows still see all of them
                // first check windows video controlers
                List<VideoControllerData> currentAvaliableVideoControllers = new List<VideoControllerData>();
                WindowsDisplayAdapters.QueryVideoControllers(currentAvaliableVideoControllers, false);
                

                int GPUsOld = AvaliableVideoControllers.Count;
                int GPUsNew = currentAvaliableVideoControllers.Count;

                Helpers.ConsolePrint("ComputeDeviceManager.CheckCount", "Video controlers GPUsOld: " + GPUsOld.ToString() + " GPUsNew:" + GPUsNew.ToString());
                */

                // check CUDA devices

                // TODO
                return false;

                //var currentCudaDevices = new List<CudaDevice>();
                //if (!Nvidia.IsSkipNvidia())
                //    Nvidia.QueryCudaDevices(ref currentCudaDevices);

                //var gpusOld = _cudaDevices.Count;
                //var gpusNew = currentCudaDevices.Count;

                //Helpers.ConsolePrint("ComputeDeviceManager.CheckCount",
                //    "CUDA GPUs count: Old: " + gpusOld + " / New: " + gpusNew);

                //return (gpusNew < gpusOld);
            }

            public static void QueryDevices(IMessageNotifier messageNotifier)
            {
                MessageNotifier = messageNotifier;
                // #0 get video controllers, used for cross checking
                WindowsDisplayAdapters.QueryVideoControllers();
                // Order important CPU Query must be first
                // #1 CPU
                Cpu.QueryCpus();
                // #2 CUDA
                NvidiaQuery nvQuery = null;
                if (ConfigManager.GeneralConfig.DeviceDetection.DisableDetectionNVIDIA)
                {
                    Helpers.ConsolePrint(Tag, "Skipping NVIDIA device detection, settings are set to disabled");
                }
                else
                {
                    ShowMessageAndStep(Tr("Querying CUDA devices"));
                    nvQuery = new NvidiaQuery();
                    nvQuery.QueryCudaDevices();
                }
                // OpenCL and AMD
                if (ConfigManager.GeneralConfig.DeviceDetection.DisableDetectionAMD)
                {
                    Helpers.ConsolePrint(Tag, "Skipping AMD device detection, settings set to disabled");
                    ShowMessageAndStep(Tr("Skip check for AMD OpenCL GPUs"));
                }
                else
                {
                    // #3 OpenCL
                    ShowMessageAndStep(Tr("Querying OpenCL devices"));
                    OpenCL.QueryOpenCLDevices();
                    // #4 AMD query AMD from OpenCL devices, get serial and add devices
                    ShowMessageAndStep(Tr("Checking AMD OpenCL GPUs"));
                    var amd = new AmdQuery(AvaliableVideoControllers);
                    AmdDevices = amd.QueryAmd(_isOpenCLQuerySuccess, _openCLQueryResult);
                }
                // #5 uncheck CPU if GPUs present, call it after we Query all devices
                AvailableDevices.UncheckCpuIfGpu();

                // TODO update this to report undetected hardware
                // #6 check NVIDIA, AMD devices count
                var nvCountMatched = false;
                {
                    var amdCount = 0;
                    var nvidiaCount = 0;
                    foreach (var vidCtrl in AvaliableVideoControllers)
                    {
                        if (vidCtrl.Name.ToLower().Contains("nvidia") && CudaUnsupported.IsSupported(vidCtrl.Name))
                        {
                            nvidiaCount += 1;
                        }
                        else if (vidCtrl.Name.ToLower().Contains("nvidia"))
                        {
                            Helpers.ConsolePrint(Tag,
                                "Device not supported NVIDIA/CUDA device not supported " + vidCtrl.Name);
                        }
                        amdCount += (vidCtrl.Name.ToLower().Contains("amd")) ? 1 : 0;
                    }

                    nvCountMatched = nvidiaCount == nvQuery?.CudaDevices?.Count;

                    Helpers.ConsolePrint(Tag,
                        nvCountMatched
                            ? "Cuda NVIDIA/CUDA device count GOOD"
                            : "Cuda NVIDIA/CUDA device count BAD!!!");
                    Helpers.ConsolePrint(Tag,
                        amdCount == AmdDevices.Count ? "AMD GPU device count GOOD" : "AMD GPU device count BAD!!!");
                }
                // allerts
                _currentNvidiaSmiDriver = GetNvidiaSmiDriver();

                // TODO: Too much GUI code here, should return list of errors to caller instead

                // if we have nvidia cards but no CUDA devices tell the user to upgrade driver
                var isNvidiaErrorShown = false; // to prevent showing twice
                var showWarning = ConfigManager.GeneralConfig.ShowDriverVersionWarning &&
                                  WindowsDisplayAdapters.HasNvidiaVideoController();
                if (showWarning && nvCountMatched &&
                    _currentNvidiaSmiDriver.IsLesserVersionThan(NvidiaMinDetectionDriver))
                {
                    isNvidiaErrorShown = true;
                    var minDriver = NvidiaMinDetectionDriver.ToString();
                    var recomendDriver = NvidiaRecomendedDriver.ToString();
                    MessageBox.Show(string.Format(
                            Tr("We have detected that your system has Nvidia GPUs, but your driver is older than {0}. In order for NiceHash Miner Legacy to work correctly you should upgrade your drivers to recommended {1} or newer. If you still see this warning after updating the driver please uninstall all your Nvidia drivers and make a clean install of the latest official driver from http://www.nvidia.com."),
                            minDriver, recomendDriver),
                        Tr("Nvidia Recomended driver"),
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                // recomended driver
                if (showWarning && _currentNvidiaSmiDriver.IsLesserVersionThan(NvidiaRecomendedDriver) &&
                    !isNvidiaErrorShown && _currentNvidiaSmiDriver.LeftPart > -1)
                {
                    var recomendDrvier = NvidiaRecomendedDriver.ToString();
                    var nvdriverString = _currentNvidiaSmiDriver.LeftPart > -1
                        ? string.Format(
                            Tr(" (current {0})"),
                            _currentNvidiaSmiDriver)
                        : "";
                    MessageBox.Show(string.Format(
                           Tr("We have detected that your Nvidia Driver is older than {0}{1}. We recommend you to update to {2} or newer."),
                            recomendDrvier, nvdriverString, recomendDrvier),
                        Tr("Nvidia Recomended driver"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                // no devices found
                if (AvailableDevices.Devices.Count <= 0)
                {
                    var result = MessageBox.Show(Tr("No supported devices are found. Select the OK button for help or cancel to continue."),
                        Tr("No Supported Devices"),
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                    if (result == DialogResult.OK)
                    {
                        Process.Start(Links.NhmNoDevHelp);
                    }
                }

                // create AMD bus ordering for Claymore
                var amdDevices = AvailableDevices.Devices.FindAll((a) => a.DeviceType == DeviceType.AMD);
                amdDevices.Sort((a, b) => a.BusID.CompareTo(b.BusID));
                for (var i = 0; i < amdDevices.Count; i++)
                {
                    amdDevices[i].IDByBus = i;
                }
                //create NV bus ordering for Claymore
                var nvDevices = AvailableDevices.Devices.FindAll((a) => a.DeviceType == DeviceType.NVIDIA);
                nvDevices.Sort((a, b) => a.BusID.CompareTo(b.BusID));
                for (var i = 0; i < nvDevices.Count; i++)
                {
                    nvDevices[i].IDByBus = i;
                }

                // get GPUs RAM sum
                // bytes
                AvailableDevices.NvidiaRamSum = 0;
                AvailableDevices.AmdRamSum = 0;
                foreach (var dev in AvailableDevices.Devices)
                {
                    if (dev.DeviceType == DeviceType.NVIDIA)
                    {
                        AvailableDevices.NvidiaRamSum += dev.GpuRam;
                    }
                    else if (dev.DeviceType == DeviceType.AMD)
                    {
                        AvailableDevices.AmdRamSum += dev.GpuRam;
                    }
                }
                // Make gpu ram needed not larger than 4GB per GPU
                var totalGpuRam = Math.Min((AvailableDevices.NvidiaRamSum + AvailableDevices.AmdRamSum) * 0.6 / 1024,
                    (double) AvailableDevices.AvailGpUs * 4 * 1024 * 1024);
                double totalSysRam = SystemSpecs.FreePhysicalMemory + SystemSpecs.FreeVirtualMemory;
                // check
                if (ConfigManager.GeneralConfig.ShowDriverVersionWarning && totalSysRam < totalGpuRam)
                {
                    Helpers.ConsolePrint(Tag, "virtual memory size BAD");
                    MessageBox.Show(Tr("NiceHash Miner Legacy recommends increasing virtual memory size so that all algorithms would work fine."),
                        Tr("Warning!"),
                        MessageBoxButtons.OK);
                }
                else
                {
                    Helpers.ConsolePrint(Tag, "virtual memory size GOOD");
                }

                // #x remove reference
                MessageNotifier = null;
            }

            #region Helpers

            private static readonly List<VideoControllerData> AvaliableVideoControllers =
                new List<VideoControllerData>();

            private static class WindowsDisplayAdapters
            {
                private static string SafeGetProperty(ManagementBaseObject mbo, string key)
                {
                    try
                    {
                        var o = mbo.GetPropertyValue(key);
                        if (o != null)
                        {
                            return o.ToString();
                        }
                    }
                    catch { }

                    return "key is null";
                }

                public static void QueryVideoControllers()
                {
                    QueryVideoControllers(AvaliableVideoControllers, true);
                }

                private static void QueryVideoControllers(List<VideoControllerData> avaliableVideoControllers,
                    bool warningsEnabled)
                {
                    var stringBuilder = new StringBuilder();
                    stringBuilder.AppendLine("");
                    stringBuilder.AppendLine("QueryVideoControllers: ");
                    var moc = new ManagementObjectSearcher("root\\CIMV2",
                        "SELECT * FROM Win32_VideoController WHERE PNPDeviceID LIKE 'PCI%'").Get();
                    var allVideoContollersOK = true;
                    foreach (var manObj in moc)
                    {
                        //Int16 ram_Str = manObj["ProtocolSupported"] as Int16; manObj["AdapterRAM"] as string
                        ulong.TryParse(SafeGetProperty(manObj, "AdapterRAM"), out var memTmp);
                        var vidController = new VideoControllerData
                        {
                            Name = SafeGetProperty(manObj, "Name"),
                            Description = SafeGetProperty(manObj, "Description"),
                            PnpDeviceID = SafeGetProperty(manObj, "PNPDeviceID"),
                            DriverVersion = SafeGetProperty(manObj, "DriverVersion"),
                            Status = SafeGetProperty(manObj, "Status"),
                            InfSection = SafeGetProperty(manObj, "InfSection"),
                            AdapterRam = memTmp
                        };
                        stringBuilder.AppendLine("\tWin32_VideoController detected:");
                        stringBuilder.AppendLine($"\t\tName {vidController.Name}");
                        stringBuilder.AppendLine($"\t\tDescription {vidController.Description}");
                        stringBuilder.AppendLine($"\t\tPNPDeviceID {vidController.PnpDeviceID}");
                        stringBuilder.AppendLine($"\t\tDriverVersion {vidController.DriverVersion}");
                        stringBuilder.AppendLine($"\t\tStatus {vidController.Status}");
                        stringBuilder.AppendLine($"\t\tInfSection {vidController.InfSection}");
                        stringBuilder.AppendLine($"\t\tAdapterRAM {vidController.AdapterRam}");

                        // check if controller ok
                        if (allVideoContollersOK && !vidController.Status.ToLower().Equals("ok"))
                        {
                            allVideoContollersOK = false;
                        }

                        avaliableVideoControllers.Add(vidController);
                    }
                    Helpers.ConsolePrint(Tag, stringBuilder.ToString());

                    if (warningsEnabled)
                    {
                        if (ConfigManager.GeneralConfig.ShowDriverVersionWarning && !allVideoContollersOK)
                        {
                            var msg = Tr("We have detected a Video Controller that is not working properly. NiceHash Miner Legacy will not be able to use this Video Controller for mining. We advise you to restart your computer, or reinstall your Video Controller drivers.");
                            foreach (var vc in avaliableVideoControllers)
                            {
                                if (!vc.Status.ToLower().Equals("ok"))
                                {
                                    msg += Environment.NewLine
                                           + string.Format(
                                               Tr("Name: {0}, Status {1}, PNPDeviceID {2}"),
                                               vc.Name, vc.Status, vc.PnpDeviceID);
                                }
                            }
                            MessageBox.Show(msg,
                               Tr("Warning! Video Controller not operating correctly"),
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                }

                public static bool HasNvidiaVideoController()
                {
                    return AvaliableVideoControllers.Any(vctrl => vctrl.Name.ToLower().Contains("nvidia"));
                }
            }

            private static class Cpu
            {
                public static void QueryCpus()
                {
                    Helpers.ConsolePrint(Tag, "QueryCpus START");
                    // get all CPUs
                    AvailableDevices.CpusCount = CpuID.GetPhysicalProcessorCount();
                    AvailableDevices.IsHyperThreadingEnabled = CpuID.IsHypeThreadingEnabled();

                    Helpers.ConsolePrint(Tag,
                        AvailableDevices.IsHyperThreadingEnabled
                            ? "HyperThreadingEnabled = TRUE"
                            : "HyperThreadingEnabled = FALSE");

                    // get all cores (including virtual - HT can benefit mining)
                    var threadsPerCpu = CpuID.GetVirtualCoresCount() / AvailableDevices.CpusCount;

                    if (!Helpers.Is64BitOperatingSystem)
                    {
                        if (ConfigManager.GeneralConfig.ShowDriverVersionWarning)
                        {
                            MessageBox.Show(Tr("NiceHash Miner Legacy works only on 64-bit version of OS for CPU mining. CPU mining will be disabled."),
                                Tr("Warning!"),
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }

                        AvailableDevices.CpusCount = 0;
                    }

                    if (threadsPerCpu * AvailableDevices.CpusCount > 64)
                    {
                        if (ConfigManager.GeneralConfig.ShowDriverVersionWarning)
                        {
                            MessageBox.Show(Tr("NiceHash Miner Legacy does not support more than 64 virtual cores. CPU mining will be disabled."),
                               Tr("Warning!"),
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }

                        AvailableDevices.CpusCount = 0;
                    }

                    // TODO important move this to settings
                    var threadsPerCpuMask = threadsPerCpu;
                    Globals.ThreadsPerCpu = threadsPerCpu;

                    if (CpuUtils.IsCpuMiningCapable())
                    {
                        if (AvailableDevices.CpusCount == 1)
                        {
                            AvailableDevices.Devices.Add(
                                new CpuComputeDevice(0, "CPU0", CpuID.GetCpuName().Trim(), threadsPerCpu, 0,
                                    ++CpuCount)
                            );
                        }
                        else if (AvailableDevices.CpusCount > 1)
                        {
                            for (var i = 0; i < AvailableDevices.CpusCount; i++)
                            {
                                AvailableDevices.Devices.Add(
                                    new CpuComputeDevice(i, "CPU" + i, CpuID.GetCpuName().Trim(), threadsPerCpu,
                                        CpuID.CreateAffinityMask(i, threadsPerCpuMask), ++CpuCount)
                                );
                            }
                        }
                    }

                    Helpers.ConsolePrint(Tag, "QueryCpus END");
                }
            }

            //private static List<CudaDevice> _cudaDevices = new List<CudaDevice>();

            private static OpenCLDeviceDetectionResult _openCLQueryResult;
            private static bool _isOpenCLQuerySuccess = false;

            private static class OpenCL
            {
                public static void QueryOpenCLDevices()
                {

                    Helpers.ConsolePrint(Tag, "QueryOpenCLDevices START");

                    string _queryOpenCLDevicesString = "";
                    try
                    {
                        _queryOpenCLDevicesString = DeviceDetection.GetOpenCLDevices();
                        _openCLQueryResult = JsonConvert.DeserializeObject<OpenCLDeviceDetectionResult>(_queryOpenCLDevicesString, Globals.JsonSettings);
                    }
                    catch (Exception ex)
                    {
                        // TODO print AMD detection string
                        Helpers.ConsolePrint(Tag, "AMDOpenCLDeviceDetection threw Exception: " + ex.Message);
                        _openCLQueryResult = null;
                    }

                    if (_openCLQueryResult == null)
                    {
                        Helpers.ConsolePrint(Tag,
                            "AMDOpenCLDeviceDetection found no devices. AMDOpenCLDeviceDetection returned: " +
                            _queryOpenCLDevicesString);
                    }
                    else /*if(_openCLQueryResult.ErrorString == "" || _openCLQueryResult.Status == "OK")*/
                    {
                        _isOpenCLQuerySuccess = true;
                        var stringBuilder = new StringBuilder();
                        stringBuilder.AppendLine("");
                        stringBuilder.AppendLine("AMDOpenCLDeviceDetection found devices success:");
                        foreach (var oclElem in _openCLQueryResult.Platforms)
                        {
                            stringBuilder.AppendLine($"\tFound devices for platform: {oclElem.PlatformName}");
                            foreach (var oclDev in oclElem.Devices)
                            {
                                stringBuilder.AppendLine("\t\tDevice:");
                                stringBuilder.AppendLine($"\t\t\tDevice ID {oclDev.DeviceID}");
                                stringBuilder.AppendLine($"\t\t\tDevice NAME {oclDev._CL_DEVICE_NAME}");
                                stringBuilder.AppendLine($"\t\t\tDevice TYPE {oclDev._CL_DEVICE_TYPE}");
                            }
                        }
                        Helpers.ConsolePrint(Tag, stringBuilder.ToString());
                    }
                    Helpers.ConsolePrint(Tag, "QueryOpenCLDevices END");
                }
            }

            public static List<OpenCLDevice> AmdDevices = new List<OpenCLDevice>();

            #endregion Helpers
        }

        public static class SystemSpecs
        {
            public static ulong FreePhysicalMemory;
            public static ulong FreeSpaceInPagingFiles;
            public static ulong FreeVirtualMemory;
            public static uint LargeSystemCache;
            public static uint MaxNumberOfProcesses;
            public static ulong MaxProcessMemorySize;

            public static uint NumberOfLicensedUsers;
            public static uint NumberOfProcesses;
            public static uint NumberOfUsers;
            public static uint OperatingSystemSKU;

            public static ulong SizeStoredInPagingFiles;

            public static uint SuiteMask;

            public static ulong TotalSwapSpaceSize;
            public static ulong TotalVirtualMemorySize;
            public static ulong TotalVisibleMemorySize;


            public static void QueryAndLog()
            {
                var winQuery = new ObjectQuery("SELECT * FROM Win32_OperatingSystem");

                var searcher = new ManagementObjectSearcher(winQuery);

                foreach (ManagementObject item in searcher.Get())
                {
                    if (item["FreePhysicalMemory"] != null)
                        ulong.TryParse(item["FreePhysicalMemory"].ToString(), out FreePhysicalMemory);
                    if (item["FreeSpaceInPagingFiles"] != null)
                        ulong.TryParse(item["FreeSpaceInPagingFiles"].ToString(), out FreeSpaceInPagingFiles);
                    if (item["FreeVirtualMemory"] != null)
                        ulong.TryParse(item["FreeVirtualMemory"].ToString(), out FreeVirtualMemory);
                    if (item["LargeSystemCache"] != null)
                        uint.TryParse(item["LargeSystemCache"].ToString(), out LargeSystemCache);
                    if (item["MaxNumberOfProcesses"] != null)
                        uint.TryParse(item["MaxNumberOfProcesses"].ToString(), out MaxNumberOfProcesses);
                    if (item["MaxProcessMemorySize"] != null)
                        ulong.TryParse(item["MaxProcessMemorySize"].ToString(), out MaxProcessMemorySize);
                    if (item["NumberOfLicensedUsers"] != null)
                        uint.TryParse(item["NumberOfLicensedUsers"].ToString(), out NumberOfLicensedUsers);
                    if (item["NumberOfProcesses"] != null)
                        uint.TryParse(item["NumberOfProcesses"].ToString(), out NumberOfProcesses);
                    if (item["NumberOfUsers"] != null)
                        uint.TryParse(item["NumberOfUsers"].ToString(), out NumberOfUsers);
                    if (item["OperatingSystemSKU"] != null)
                        uint.TryParse(item["OperatingSystemSKU"].ToString(), out OperatingSystemSKU);
                    if (item["SizeStoredInPagingFiles"] != null)
                        ulong.TryParse(item["SizeStoredInPagingFiles"].ToString(), out SizeStoredInPagingFiles);
                    if (item["SuiteMask"] != null) uint.TryParse(item["SuiteMask"].ToString(), out SuiteMask);
                    if (item["TotalSwapSpaceSize"] != null)
                        ulong.TryParse(item["TotalSwapSpaceSize"].ToString(), out TotalSwapSpaceSize);
                    if (item["TotalVirtualMemorySize"] != null)
                        ulong.TryParse(item["TotalVirtualMemorySize"].ToString(), out TotalVirtualMemorySize);
                    if (item["TotalVisibleMemorySize"] != null)
                        ulong.TryParse(item["TotalVisibleMemorySize"].ToString(), out TotalVisibleMemorySize);
                    // log
                    Helpers.ConsolePrint("SystemSpecs", $"FreePhysicalMemory = {FreePhysicalMemory}");
                    Helpers.ConsolePrint("SystemSpecs", $"FreeSpaceInPagingFiles = {FreeSpaceInPagingFiles}");
                    Helpers.ConsolePrint("SystemSpecs", $"FreeVirtualMemory = {FreeVirtualMemory}");
                    Helpers.ConsolePrint("SystemSpecs", $"LargeSystemCache = {LargeSystemCache}");
                    Helpers.ConsolePrint("SystemSpecs", $"MaxNumberOfProcesses = {MaxNumberOfProcesses}");
                    Helpers.ConsolePrint("SystemSpecs", $"MaxProcessMemorySize = {MaxProcessMemorySize}");
                    Helpers.ConsolePrint("SystemSpecs", $"NumberOfLicensedUsers = {NumberOfLicensedUsers}");
                    Helpers.ConsolePrint("SystemSpecs", $"NumberOfProcesses = {NumberOfProcesses}");
                    Helpers.ConsolePrint("SystemSpecs", $"NumberOfUsers = {NumberOfUsers}");
                    Helpers.ConsolePrint("SystemSpecs", $"OperatingSystemSKU = {OperatingSystemSKU}");
                    Helpers.ConsolePrint("SystemSpecs", $"SizeStoredInPagingFiles = {SizeStoredInPagingFiles}");
                    Helpers.ConsolePrint("SystemSpecs", $"SuiteMask = {SuiteMask}");
                    Helpers.ConsolePrint("SystemSpecs", $"TotalSwapSpaceSize = {TotalSwapSpaceSize}");
                    Helpers.ConsolePrint("SystemSpecs", $"TotalVirtualMemorySize = {TotalVirtualMemorySize}");
                    Helpers.ConsolePrint("SystemSpecs", $"TotalVisibleMemorySize = {TotalVisibleMemorySize}");
                }
            }
        }
    }
}
