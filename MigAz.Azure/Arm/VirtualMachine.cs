// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using MigAz.Azure.Interface;
using MigAz.Core.Interface;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MigAz.Azure.Arm
{
    public class VirtualMachine : ArmResource, IVirtualMachine
    {
        private List<IArmDisk> _DataDisks = new List<IArmDisk>();
        private IArmDisk _OSVirtualHardDisk;
        private List<NetworkInterface> _NetworkInterfaceCards = new List<NetworkInterface>();
        private VMSize _VMSize;


        private VirtualMachine() : base(null, null) { }

        public VirtualMachine(AzureSubscription azureSubscription, JToken resourceToken) : base(azureSubscription, resourceToken)
        {
            this.AzureSubscription.LogProvider.WriteLog("Arm.VirutalMachine Ctor", "Constructing OS Disk");

            if (ResourceToken["properties"]["storageProfile"]["osDisk"]["vhd"] == null)
            {
                // Find and Link to Managed Disk
                if (ResourceToken["properties"]["storageProfile"]["osDisk"]["managedDisk"] == null)
                {
                    string managedDiskName = ResourceToken["properties"]["storageProfile"]["osDisk"]["name"].ToString();
                    this.AzureSubscription.LogProvider.WriteLog("Arm.VirutalMachine Ctor", "Seeking Managed Disk By Name '" + managedDiskName + "'.  Managed Disk object not available for By Id seek.");

                    ManagedDisk osDisk = azureSubscription.SeekManagedDiskByName(managedDiskName);
                    osDisk.SetParentVirtualMachine(this, ResourceToken["properties"]["storageProfile"]["osDisk"]);
                    _OSVirtualHardDisk = osDisk;
                }
                else
                {
                    string managedDiskId = ResourceToken["properties"]["storageProfile"]["osDisk"]["managedDisk"]["id"].ToString();
                    this.AzureSubscription.LogProvider.WriteLog("Arm.VirutalMachine Ctor", "Seeking Managed Disk By Name '" + managedDiskId + "'.");

                    ManagedDisk osDisk = azureSubscription.SeekManagedDiskById(managedDiskId);
                    osDisk.SetParentVirtualMachine(this, ResourceToken["properties"]["storageProfile"]["osDisk"]);
                    _OSVirtualHardDisk = osDisk;
                }
            }
            else
            {
                _OSVirtualHardDisk = new ClassicDisk(this, ResourceToken["properties"]["storageProfile"]["osDisk"]);
            }

            foreach (JToken dataDiskToken in ResourceToken["properties"]["storageProfile"]["dataDisks"])
            {
                this.AzureSubscription.LogProvider.WriteLog("Arm.VirutalMachine Ctor", "Constructing Data Disk");

                if (dataDiskToken["vhd"] == null)
                {
                    // Find and Link to Managed Disk
                    if (dataDiskToken["managedDisk"] == null)
                    {
                        string managedDiskName = dataDiskToken["name"].ToString();
                        this.AzureSubscription.LogProvider.WriteLog("Arm.VirutalMachine Ctor", "Seeking Managed Disk By Name '" + managedDiskName + "'.  Managed Disk object not available for By Id seek.");

                        ManagedDisk dataDisk = azureSubscription.SeekManagedDiskByName(managedDiskName);
                        dataDisk.SetParentVirtualMachine(this, dataDiskToken);
                        _DataDisks.Add(dataDisk);
                    }
                    else
                    {
                        string managedDiskId = dataDiskToken["managedDisk"]["id"].ToString();
                        this.AzureSubscription.LogProvider.WriteLog("Arm.VirutalMachine Ctor", "Seeking Managed Disk By Name '" + managedDiskId + "'.");

                        ManagedDisk dataDisk = azureSubscription.SeekManagedDiskById(managedDiskId);
                        dataDisk.SetParentVirtualMachine(this, dataDiskToken);
                        _DataDisks.Add(dataDisk);
                    }
                }
                else
                {
                    _DataDisks.Add(new ClassicDisk(this, dataDiskToken));
                }
            }
        }

        public bool HasPlan
        {
            get
            {
                return ResourceToken["plan"] != null;
            }
        }

        public string Type => (string)ResourceToken["type"];
        public Guid VmId => new Guid((string)ResourceToken["properties"]["vmId"]);
        private string VmSizeString => (string)ResourceToken["properties"]["hardwareProfile"]["vmSize"];
        public VMSize VmSize
        {
            get { return _VMSize; }
            set { _VMSize = value;  }
        }

        public string OSVirtualHardDiskOS => (string)ResourceToken["properties"]["storageProfile"]["osDisk"]["osType"];

        internal string AvailabilitySetId
        {
            get
            {
                try
                {
                    return (string)ResourceToken["properties"]["availabilitySet"]["id"];
                }
                catch (NullReferenceException)
                {
                    return String.Empty;
                }
            }
        }

        public List<IArmDisk> DataDisks => _DataDisks;
        public IArmDisk OSVirtualHardDisk => _OSVirtualHardDisk;

        public List<NetworkInterface> NetworkInterfaces => _NetworkInterfaceCards;

        public AvailabilitySet AvailabilitySet
        {
            get; private set;
        }
        public NetworkInterface PrimaryNetworkInterface
        {
            get
            {
                foreach (NetworkInterface networkInterface in this.NetworkInterfaces)
                {
                    if (networkInterface.IsPrimary)
                        return networkInterface;
                }

                return null;
            }
        }

        internal new async Task InitializeChildrenAsync()
        {
            await base.InitializeChildrenAsync();

            if (this.AvailabilitySetId != String.Empty)
                this.AvailabilitySet = this.AzureSubscription.GetAzureARMAvailabilitySet(this.AvailabilitySetId);

            if (this.AvailabilitySet != null)
                this.AvailabilitySet.VirtualMachines.Add(this);


            await this.OSVirtualHardDisk.InitializeChildrenAsync();

            foreach (IArmDisk dataDisk in this.DataDisks)
            {
                if (dataDisk.GetType() == typeof(Arm.ClassicDisk))
                {
                    ClassicDisk classicDisk = (Arm.ClassicDisk)dataDisk;
                    await classicDisk.InitializeChildrenAsync();
                }
            }

            foreach (JToken networkInterfaceToken in ResourceToken["properties"]["networkProfile"]["networkInterfaces"])
            {
                NetworkInterface networkInterface = await this.AzureSubscription.GetAzureARMNetworkInterface((string)networkInterfaceToken["id"]);
                networkInterface.VirtualMachine = this;
                _NetworkInterfaceCards.Add(networkInterface);
            }

            // Seek the VmSize object that corresponds to the VmSize String obtained from the VM Json
            if (this.ResourceGroup != null && this.ResourceGroup.Location != null)
            {
                this.VmSize = this.ResourceGroup.Location.SeekVmSize(this.VmSizeString);
            }

            return;
        }

        public async Task Refresh()
        {
            base.SetResourceToken(await this.AzureSubscription.GetAzureArmVirtualMachineDetail(this));
        }

        public override string ToString()
        {
            return this.Name;
        }
    }
}

