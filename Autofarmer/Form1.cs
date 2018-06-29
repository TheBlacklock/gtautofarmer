﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices; // For the DllImport
using System.Threading;
using System.Windows.Forms;
using System.IO;
using System.Diagnostics; // Process

namespace Autofarmer {
	public partial class Autofarmer : Form {
		private class WS { // Windows structs, different formats of data we'll be receiving
						   // LayoutKind.Sequential to store the fields correctly ordered in memory
						   // Pack=1 if we need to read a byte at a time
			[StructLayout(LayoutKind.Sequential, Pack = 1)]
			public struct SYSTEM_HANDLE_INFORMATION { // Returned data from SystemHandleInformation, a handle
				public int ProcessID;
				public byte ObjectTypeNumber;
				public byte Flags; // 0x01 = PROTECT_FROM_CLOSE, 0x02 = INHERIT
				public ushort Handle;
				public int Object_Pointer;
				public UInt32 GrantedAccess;
			}

			[StructLayout(LayoutKind.Sequential)]
			public struct OBJECT_BASIC_INFORMATION { // Information Class 0
				public int Attributes;
				public int GrantedAccess;
				public int HandleCount;
				public int PointerCount;
				public int PagedPoolUsage;
				public int NonPagedPoolUsage;
				public int Reserved1;
				public int Reserved2;
				public int Reserved3;
				public int NameInformationLength;
				public int TypeInformationLength;
				public int SecurityDescriptorLength;
				public System.Runtime.InteropServices.ComTypes.FILETIME CreateTime;
			}

			[StructLayout(LayoutKind.Sequential)]
			public struct OBJECT_NAME_INFORMATION { // Information Class 1
				public UNICODE_STRING Name;
			}

			[StructLayout(LayoutKind.Sequential, Pack = 1)]
			public struct UNICODE_STRING {
				public ushort Length;
				public ushort MaximumLength;
				public IntPtr Buffer;
			}
		}

		[DllImport("ntdll.dll")]
		// NtQuerySystemInformation gives us data about all the handlers in the system
		private static extern uint NtQuerySystemInformation(uint SystemInformationClass, IntPtr SystemInformation,
			int SystemInformationLength, ref int nLength);

		[DllImport("kernel32.dll")]
		// dwDesiredAccess sets the process access rights (docs.microsoft.com/en-us/windows/desktop/ProcThread/process-security-and-access-rights)
		// if bInheritHandle is true, processes created by this process will inherit the handle (we don't need this, maybe just set it as a bool)
		// dwProcessId is the PID of the process we want to open with those access rights
		public static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

		[DllImport("kernel32.dll")]
		// Returns us a handle to the current process
		public static extern IntPtr GetCurrentProcess();

		[DllImport("kernel32.dll")]
		// Closes a handle
		public static extern int CloseHandle(IntPtr hObject);

		[DllImport("ntdll.dll")]
		// Retrieves information about an object
		// Handle is the object's handle we're getting information from
		// ObjectInformationClass is the type of information we want; ObjectBasicInformation/ObjectTypeInformation, undocumented ObjectNameInformation?
		// ObjectInformation is the buffer where the data is returned to, ObjectInformationLength is the size of that buffer
		// returnLength is a variable where NtQueryObject writes the size of the information returned to us
		public static extern int NtQueryObject(IntPtr Handle, int ObjectInformationClass, IntPtr ObjectInformation,
			int ObjectInformationLength, ref int returnLength);

		[DllImport("kernel32.dll", SetLastError = true)] // Display errors
		[return: MarshalAs(UnmanagedType.Bool)]
		// DuplicateHandle duplicates a handle from an external process to ours
		// hSourceProcessHandle is the process we duplicate from, hSourceHandle is the handle we duplicate
		// hTargetProcessHandle is the process we duplicate to, lpTargetHandle is a pointer to a var that receives the new handler
		private static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, ushort hSourceHandle, IntPtr hTargetProcessHandle,
			out IntPtr lpTargetHandle, uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwOptions);

		[DllImport("kernel32.dll")]
		// Access rights, inheritance bool, ID of thread
		static extern IntPtr OpenThread(int dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

		[DllImport("kernel32.dll")]
		// Thread to suspend
		static extern uint SuspendThread(IntPtr hThread);

		[DllImport("kernel32.dll")]
		// Thread to resume
		static extern int ResumeThread(IntPtr hThread);

		[DllImport("user32.dll")]
		static extern int SetWindowText(IntPtr hWnd, string text);

		private List<Process> processes = new List<Process>(); // List of open Growtopia processes
		private List<int> multiboxes = new List<int>();

		CheckBox checkbox; // "Select all" checkbox
		private bool selectAllChecked = false;

		private string _growtopiaPath = "";
		private string GrowtopiaPath {
			get {
				return _growtopiaPath;
			}
			set {
				if (File.Exists(value)) {
					_growtopiaPath = value;
					fileSelectDialog.InitialDirectory = _growtopiaPath;
					fileNameDisplayer.Text = _growtopiaPath;
				} else {
					MessageBox.Show("Incorrect file specified", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
				}
			}
		}

		private static string ViewHandleName(WS.SYSTEM_HANDLE_INFORMATION shHandle, Process process) {
			// handleInfoStruct is the struct that contains data about our handle
			// targetProcess is the process where the handle resides
			// DUP_HANDLE (0x40) might also work
			IntPtr sourceProcessHandle = OpenProcess(0x1F0FFF, false, process.Id);
			IntPtr targetHandle = IntPtr.Zero;

			// We create a duplicate of the handle so that we can query more information about it
			if (!DuplicateHandle(sourceProcessHandle, shHandle.Handle, GetCurrentProcess(), out targetHandle, 0, false, 0x2)) {
				return null;
			}

			// Buffers that the query results get sent to
			IntPtr basicQueryData = IntPtr.Zero;

			// Query result structs
			WS.OBJECT_BASIC_INFORMATION basicInformationStruct = new WS.OBJECT_BASIC_INFORMATION();
			WS.OBJECT_NAME_INFORMATION nameInformationStruct = new WS.OBJECT_NAME_INFORMATION();

			basicQueryData = Marshal.AllocHGlobal(Marshal.SizeOf(basicInformationStruct));

			int nameInfoLength = 0; // Size of information returned to us
			NtQueryObject(targetHandle, 0, basicQueryData, Marshal.SizeOf(basicInformationStruct), ref nameInfoLength);

			// Insert buffer data into a struct and free the buffer
			basicInformationStruct = (WS.OBJECT_BASIC_INFORMATION)Marshal.PtrToStructure(basicQueryData, basicInformationStruct.GetType());
			Marshal.FreeHGlobal(basicQueryData);

			// The basicInformationStruct contains data about the name's length
			// TODO: We could probably skip querying for OBJECT_BASIC_INFORMATION
			nameInfoLength = basicInformationStruct.NameInformationLength;

			// Allocate buffer for the name now that we know its size
			IntPtr nameQueryData = Marshal.AllocHGlobal(nameInfoLength);

			// Object information class: 1
			// If it's incorrect, it returns STATUS_INFO_LENGTH_MISMATCH (0xc0000004)
			int result;
			while ((uint)(result = NtQueryObject(targetHandle, 1, nameQueryData, nameInfoLength, ref nameInfoLength)) == 0xc0000004) {
				Marshal.FreeHGlobal(nameQueryData);
				nameQueryData = Marshal.AllocHGlobal(nameInfoLength);
			}
			nameInformationStruct = (WS.OBJECT_NAME_INFORMATION)Marshal.PtrToStructure(nameQueryData, nameInformationStruct.GetType());

			IntPtr handlerName;

			if (Is64Bits()) {
				handlerName = new IntPtr(Convert.ToInt64(nameInformationStruct.Name.Buffer.ToString(), 10) >> 32);
			} else {
				handlerName = nameInformationStruct.Name.Buffer;
			}

			if (handlerName != IntPtr.Zero) {
				byte[] baTemp2 = new byte[nameInfoLength];
				try {
					Marshal.Copy(handlerName, baTemp2, 0, nameInfoLength);
					return Marshal.PtrToStringUni(Is64Bits() ? new IntPtr(handlerName.ToInt64()) : new IntPtr(handlerName.ToInt32()));
				}
				catch (AccessViolationException) {
					return null;
				}
				finally {
					Marshal.FreeHGlobal(nameQueryData);
					CloseHandle(targetHandle);
				}
			}
			return null;
		}

		// Closes all needed mutant handles in given process
		private void CloseProcessHandles(Process growtopia) { // We need to remove handlers from the last process
			Console.WriteLine("Starting handle magic...");
			statusMessage.Text = "Querying system handle information...";
			int nLength = 0;
			IntPtr handlePointer = IntPtr.Zero;
			int sysInfoLength = 0x10000; // How much to allocate to returned data
			IntPtr infoPointer = Marshal.AllocHGlobal(sysInfoLength);
			// 0x10 = SystemHandleInformation, an undocumented SystemInformationClass
			uint result; // NtQuerySystemInformation won't give us the correct buffer size, so we guess it
						 // Assign result of NtQuerySystemInformation to this variable and check if the buffer size is correct
						 // If it's incorrect, it returns STATUS_INFO_LENGTH_MISMATCH (0xc0000004)
			while ((result = NtQuerySystemInformation(0x10, infoPointer, sysInfoLength, ref nLength)) == 0xc0000004) {
				sysInfoLength = nLength;
				Marshal.FreeHGlobal(infoPointer);
				infoPointer = Marshal.AllocHGlobal(nLength);
			}

			byte[] baTemp = new byte[nLength];
			// Copy the data from unmanaged memory to managed 1-byte uint array
			Marshal.Copy(infoPointer, baTemp, 0, nLength);
			// Do we even need the two statements above??? Look into this later.

			long sysHandleCount = 0; // How many handles there are total
			if (Is64Bits()) {
				sysHandleCount = Marshal.ReadInt64(infoPointer);
				handlePointer = new IntPtr(infoPointer.ToInt64() + 8); // Points in bits at the start of a handle
			} else {
				sysHandleCount = Marshal.ReadInt32(infoPointer);
				handlePointer = new IntPtr(infoPointer.ToInt32() + 4); // Ignores 4 first bits instead of 8
			}

			statusMessage.Text = "Query received, processing the " + sysHandleCount + " results.";

			WS.SYSTEM_HANDLE_INFORMATION handleInfoStruct; // The struct to hold info about a single handler

			List<WS.SYSTEM_HANDLE_INFORMATION> handles = new List<WS.SYSTEM_HANDLE_INFORMATION>();
			for (long i = 0; i < sysHandleCount; i++) { // Iterate over handle structs in the handle struct list
				handleInfoStruct = new WS.SYSTEM_HANDLE_INFORMATION();
				if (Is64Bits()) {
					handleInfoStruct = (WS.SYSTEM_HANDLE_INFORMATION)Marshal.PtrToStructure(handlePointer, handleInfoStruct.GetType()); // Convert to struct
					handlePointer = new IntPtr(handlePointer.ToInt64() + Marshal.SizeOf(handleInfoStruct) + 8); // point 8 bits forward to the next handle
				} else {
					handleInfoStruct = (WS.SYSTEM_HANDLE_INFORMATION)Marshal.PtrToStructure(handlePointer, handleInfoStruct.GetType());
					handlePointer = new IntPtr(handlePointer.ToInt64() + Marshal.SizeOf(handleInfoStruct));
				}

				if (handleInfoStruct.ProcessID != growtopia.Id) { // Check if current handler is from Growtopia
					continue; // If it's not from Growtopia, just skip it
				}

				string handleName = ViewHandleName(handleInfoStruct, growtopia);
				if (@"\Sessions\1\BaseNamedObjects\Growtopia" != handleName) {
					continue; // This is not a handle we're looking for
				}
				handles.Add(handleInfoStruct);
				Console.WriteLine("PID {0,7} Pointer {1,12} Type {2,4} Name {3}", handleInfoStruct.ProcessID.ToString(),
																				  handleInfoStruct.Object_Pointer.ToString(),
																				  handleInfoStruct.ObjectTypeNumber.ToString(),
																				  handleName);
			}

			Console.WriteLine("Closing mutexes?");
			foreach (WS.SYSTEM_HANDLE_INFORMATION handle in handles) {
				CloseMutex(handle);
			}

			statusMessage.Text = "Query finished, " + sysHandleCount + " results processed.";
			Console.WriteLine("Handle closed.");
		}

		private void SuspendProcess(Process process) {
			foreach (ProcessThread pT in process.Threads) {
				// SUSPEND_RESUME = 0x0002
				IntPtr pOpenThread = OpenThread(0x0002, false, (uint)pT.Id);

				if (pOpenThread == IntPtr.Zero) {
					continue;
				}

				SuspendThread(pOpenThread);
				CloseHandle(pOpenThread);
			}
		}
		private void ResumeProcess(Process process) {
			foreach (ProcessThread pT in process.Threads) {
				// SUSPEND_RESUME = 0x0002
				IntPtr pOpenThread = OpenThread(0x0002, false, (uint)pT.Id);

				if (pOpenThread == IntPtr.Zero) {
					continue;
				}

				var suspendCount = 0;
				do {
					suspendCount = ResumeThread(pOpenThread);
				} while (suspendCount > 0);

				CloseHandle(pOpenThread);
			}
		}

		private void CloseMutex(WS.SYSTEM_HANDLE_INFORMATION handle) {
			IntPtr targetHandle;
			// DUPLICATE_CLOSE_SOURCE = 0x1
			// GetCurrentProcess(), out targetHandle ======> Set target process to null for success
			if (!DuplicateHandle(Process.GetProcessById(handle.ProcessID).Handle, handle.Handle, IntPtr.Zero, out targetHandle, 0, false, 0x1)) {
				MessageBox.Show("Failed to close mutex: " + Marshal.GetLastWin32Error(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
			Console.WriteLine("Mutex was killed");
		}

		public Autofarmer() {
			InitializeComponent(); // Set size etc. and run Autofarmer_Load()
		}

		private void Autofarmer_Load(object sender, EventArgs e) {
			// Set as Growtopia's default path

			GrowtopiaPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Appdata\Local\Growtopia\Growtopia.exe";

			// Dropdown list default values

			autoFarmerType.SelectedIndex = 0;
			multiboxToggle.SelectedIndex = 0;

			// Create checkbox for "Select all" in processList

			checkbox = new CheckBox();
			checkbox.Size = new System.Drawing.Size(15, 15);
			checkbox.BackColor = Color.Transparent;

			checkbox.Padding = new Padding(0);
			checkbox.Margin = new Padding(0);
			checkbox.Text = "";

			processList.Controls.Add(checkbox);
			DataGridViewHeaderCell header = processList.Columns["Checkbox"].HeaderCell;
			checkbox.Location = new Point(12, 10);
			checkbox.CheckedChanged += new EventHandler(SelectAll);
		}

		private void SelectAll(object sender, EventArgs e) {
			selectAllChecked = !selectAllChecked;

			foreach (DataGridViewRow row in processList.Rows) {
				DataGridViewCheckBoxCell checkbox = (DataGridViewCheckBoxCell)row.Cells["Checkbox"];
				checkbox.Value = selectAllChecked;
				checkbox.Value = !selectAllChecked;
				checkbox.Value = selectAllChecked;
			}

			processList.RefreshEdit();
		}

		private void changeAutofarmer(object sender, EventArgs e) {
			foreach (DataGridViewRow row in processList.Rows) {
				if (Convert.ToBoolean(row.Cells["Checkbox"].Value) == true) {
					row.Cells["Active"].Value = autoFarmerType.SelectedItem.ToString();
				}
			}
		}

		private void changeMultibox(object sender, EventArgs e) {
			foreach (DataGridViewRow row in processList.Rows) {
				if (Convert.ToBoolean(row.Cells["Checkbox"].Value) == true) {
					row.Cells["Multibox"].Value = multiboxToggle.SelectedItem.ToString() == "Enabled" ? "Yes" : "No";
				}
			}
		}

		private void SelectFile(object sender, EventArgs e) {
			DialogResult result = fileSelectDialog.ShowDialog();
			if (result == DialogResult.OK) {
				GrowtopiaPath = fileSelectDialog.FileName;
			}
		}

		private void OpenGrowtopia(object sender, EventArgs e) {
			if (GrowtopiaPath != "") { // Make sure Growtopia's path is set
				List<Process> suspendedProcesses = new List<Process>();
				if (processes.Count > 0) {
					foreach (Process process in processes) {
						SuspendProcess(process);
						suspendedProcesses.Add(process);
					}
				}
				for (int i = 0; i < numberInput.Value; i++) {
					if (i != 0) { // We must suspend the previous process
						SuspendProcess(processes[processes.Count - 1]);
						suspendedProcesses.Add(processes[processes.Count - 1]);
					}

					if (processes.Count > 0) { // We must delete the mutex from previous process
						CloseProcessHandles(processes[processes.Count - 1]); // It's already suspended so we're fine
					}

					Process growtopia = new Process(); // New growtopia process
					fileSelectButton.Enabled = false;
					openGTButton.Enabled = false;
					growtopia.StartInfo.FileName = GrowtopiaPath;
					processes.Add(growtopia);
					growtopia.Start();

					processList.Rows.Add();
					processList.Rows[processes.Count - 1].Cells["Number"].Value = processes.Count;
					processList.Rows[processes.Count - 1].Cells["Active"].Value = "None";
					processList.Rows[processes.Count - 1].Cells["Multibox"].Value = "No";
					processList.Rows[processes.Count - 1].Cells["PID"].Value = growtopia.Id;
					if (processes.Count == 6) { // Is this the 6th process?
						checkbox.Location = new Point(4, 10); // If it is, then change header checkbox location due to the scrollbar
					}
					Thread.Sleep(800);
					SetWindowText(growtopia.MainWindowHandle, "Growtopia " + processes.Count);
				}

				foreach (Process process in suspendedProcesses) {
					ResumeProcess(process);
				}
				openGTButton.Text = "Open GT (" + processes.Count + " open)";
				statusMessage.Text = "Growtopias opened!";
				openGTButton.Enabled = true;
			} else {
				MessageBox.Show("Please set a file path for Growtopia!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}

		private static bool Is64Bits() {
			return Marshal.SizeOf(typeof(IntPtr)) == 8 ? true : false;
		}

		private void debugCloseMutant(object sender, EventArgs e) {
			var x = processList.Rows[0].Cells["Checkbox"].Selected;
			Console.WriteLine(x);
		}
	}
}