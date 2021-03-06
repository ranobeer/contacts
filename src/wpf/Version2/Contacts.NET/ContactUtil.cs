/**************************************************************************\
    Copyright Microsoft Corporation. All Rights Reserved.
\**************************************************************************/

namespace Microsoft.ContactsBridge.Interop
{
    using Standard;
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Text;

    using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;
    using System.IO;
    using System.Runtime.InteropServices.ComTypes;
	using Standard.Interop;
	using Microsoft.Communications.Contacts;
	using Microsoft.Communications.Contacts.Interop;
	using System.Reflection;
	using System.Diagnostics.CodeAnalysis;

    public class MarshalableLabelCollection : IDisposable
    {
        // The managed array of LPCWSTRs as IntPtrs.
        private IntPtr[] _nativeStrings;
        // The buffer that contains the marhshalable version of the LPCWSTRs.
        private IntPtr _nativeArray;
        // Number of LPCWSTRs allocated in _nativeStrings.
        // In case of partial object creation, this is needed for accurate cleanup.
        private uint _count;

        public MarshalableLabelCollection(ICollection<string> labels)
        {
            _count = 0;
            _nativeArray = IntPtr.Zero;
            _nativeStrings = null;

            if (null != labels)
            {
                // This doesn't need to be greater than zero.
                // If this represents a 0 length array, the handle returned is NULL.
                if (labels.Count > 0)
                {
                    // Since we're allocating memory, be ready to cleanup if this throws at any point.
                    try
                    {
                        _nativeStrings = new IntPtr[labels.Count];
                        foreach (string label in labels)
                        {
                            if (null == label || 0 == label.Length)
                            {
                                throw new SchemaException("The array must not contain empty strings");
                            }
                            _nativeStrings[_count++] = Marshal.StringToCoTaskMemUni(label);
                        }

                        _nativeArray = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(IntPtr)) * _nativeStrings.Length);
                        Marshal.Copy(_nativeStrings, 0, _nativeArray, _nativeStrings.Length);
                    }
                    catch
                    {
                        // Something happened: probably either an argument or out of memory exception.
                        // The finalizer would get called, but it's better to clean up our own mess.
                        _Dispose(true);
                        throw;
                    }
                }
            }
        }

        public IntPtr MarshaledLabels
        {
            get
            {
                return _nativeArray;
            }
        }

        public uint Count
        {
            get
            {
                return _count;
            }
        }

        #region IDisposable Pattern

        public void Dispose()
        {
            _Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~MarshalableLabelCollection()
        {
            _Dispose(false);
        }

        private void _Dispose(bool disposing)
        {
            Utility.SafeCoTaskMemFree(ref _nativeArray);

            // If there's a count of strings, then there must be an array where they are stored.
            Assert.Implies(_count > 0, null != _nativeStrings);

            for (int i = 0; i < _count; ++i)
            {
                Utility.SafeCoTaskMemFree(ref _nativeStrings[i]);
            }
        }

        #endregion
    }

    public class MarshalableDoubleNullString : IDisposable
    {
        private uint _cch;
        private IntPtr _buffer;

        public MarshalableDoubleNullString(uint characterCapacity)
        {
            Realloc(characterCapacity);
        }

        public uint Capacity
        {
            get
            {
                return _cch;
            }
        }

        public IntPtr MarshaledString
        {
            get
            {
                return _buffer;
            }
        }

        public List<string> ParsedStrings
        {
            get
            {
                return ContactUtil.ParseDoubleNullString(_buffer);
            }
        }

        public void Realloc(uint cch)
        {
            if (0 == cch)
            {
                throw new ArgumentException("Can't allocate zero-sized native buffer");
            }
            _Dispose(true);
            _buffer = Marshal.AllocCoTaskMem((int)(cch * Win32Value.sizeof_WCHAR));
            _cch = cch;
        }

        #region IDisposable Pattern

        public void Dispose()
        {
            _Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~MarshalableDoubleNullString()
        {
            _Dispose(false);
        }

        private void _Dispose(bool disposing)
        {
            Utility.SafeCoTaskMemFree(ref _buffer);
        }

        #endregion
    }

    /// <summary>
    /// Key tokens for the opaque string Ids that comprise ContactIds and PersonIds.
    /// </summary>
    public enum ContactIdToken
    {
        Guid,
        Path,
    }

    /// <summary>
    /// Static utility class to ease working with the native COM IContact interfaces.
    /// </summary>
    public static class ContactUtil
    {
        private static readonly Dictionary<ContactIdToken, string> _tokenMap;

		private static string _assemblyName;
		public static string DllName { get { return _assemblyName; } }
		[SuppressMessage("Microsoft.Performance", "CA1804:RemoveUnusedLocals", MessageId = "app")]
		public static Uri GetResourceUri(string resourceName)
		{
			Assert.IsNeitherNullNorEmpty(resourceName);

			if (null == _assemblyName)
			{
				// WPF Dlls need to be loaded for the pack: uri syntax to work.
				var app = System.Windows.Application.Current;

				_assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
			}

			return new Uri(@"pack://application:,,,/" + _assemblyName + @";Component/Files/" + resourceName);
		}

        #region externs
        // This function throws an exception on failure.
        [DllImport("shell32.dll", PreserveSig = false)]
        static extern void SHGetKnownFolderPath([In] ref Guid rfid, int dwFlags, [In] IntPtr hToken, [Out]  out IntPtr ppszPath);

        [DllImport("shlwapi.dll", PreserveSig = false)]
        static extern void ConnectToConnectionPoint(
            [In, MarshalAs(UnmanagedType.IUnknown)] object punk,
            [In] ref Guid riidEvent,
            [In] uint fConnect,
            [In, MarshalAs(UnmanagedType.IUnknown)] object punkTarget,
            ref uint pdwCookie, out IConnectionPoint ppcpOut);
        #endregion

        static ContactUtil()
        {
            _tokenMap = new Dictionary<ContactIdToken, string>();
            _tokenMap.Add(ContactIdToken.Path, "/PATH:");
            _tokenMap.Add(ContactIdToken.Guid, "/GUID:");
        }

		public static string ExpandRootDirectory(string rootDirectory)
		{
			if (rootDirectory.StartsWith("*", StringComparison.Ordinal))
			{
				rootDirectory = Path.Combine(GetContactsFolder(), rootDirectory.Substring(1).TrimStart('\\', '/'));
				Assert.IsTrue(Path.IsPathRooted(rootDirectory));
			}

			// This might throw, but I want that exception to propagate out.
			return Path.GetFullPath(rootDirectory).TrimEnd('\\', '/');
		}


        private static DateTime DateTimeFromFILETIME(FILETIME ft)
        {
            ulong l = (uint)ft.dwHighDateTime;
            l <<= 32;
            l |= (uint)ft.dwLowDateTime;
            DateTime dt = DateTime.FromFileTimeUtc((long)l);
            return dt;
        }

        public static HRESULT CommitContact(IContact contact, bool force)
        {
			Verify.IsNotNull<IContact>(contact, "contact");

            HRESULT hr = contact.CommitChanges(ContactValue.CGD_DEFAULT);
            // If this failed because of conflicting changes then try going directly to the file at the caller's behest.
            if (force
                && ((HRESULT)Win32Error.ERROR_NESTING_NOT_ALLOWED == hr
                    || (HRESULT)Win32Error.ERROR_FILE_NOT_FOUND == hr))
            {
                string path;
                GetPath(contact, out path).ThrowIfFailed("Conflicting changes were encountered but an error occurred trying to bypass them.");
                using (FileStream fstream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                {
                    using (ManagedIStream istream = new ManagedIStream(fstream))
                    {
                        ((IPersistStream)contact).Save(istream, true);
                        hr = HRESULT.S_OK;
                    }
                }
            }
            return hr;
        }

        public static HRESULT CreateArrayNode(INativeContactProperties contact, string arrayName, bool appendNode, out string node)
        {
            node = null;
            Verify.IsNotNull(contact, "contact");

            HRESULT hr = HRESULT.S_OK;
            StringBuilder sb = new StringBuilder((int)Win32Value.MAX_PATH);
            uint convertedAppend = appendNode ? Win32Value.TRUE : Win32Value.FALSE;
            uint cch;

            hr = contact.CreateArrayNode(arrayName, ContactValue.CGD_DEFAULT, convertedAppend, sb, (uint)sb.Capacity, out cch);
            
            // If we didn't have enough space for the node the first time through, try the bigger size.
            if ((HRESULT)Win32Error.ERROR_INSUFFICIENT_BUFFER == hr)
            {
                sb.EnsureCapacity((int)cch);
                hr = contact.CreateArrayNode(arrayName, ContactValue.CGD_DEFAULT, convertedAppend, sb, (uint)sb.Capacity, out cch);

                // If this failed a second time, it shouldn't be because of an insufficient buffer.
                Assert.Implies(hr.Failed(), (HRESULT)Win32Error.ERROR_INSUFFICIENT_BUFFER != hr);
            }

            if (hr.Succeeded())
            {
                node = sb.ToString();
            }

            return hr;
        }

		public static HRESULT DeleteArrayNode(INativeContactProperties contact, string nodeName)
        {
            Verify.IsNotNull(contact, "contact");
			Verify.IsNotNull(nodeName, "nodeName");

            // COM APIs don't check for this.  DeleteProperty should be used in this case.
            if (!nodeName.EndsWith("]"))
            {
                return (HRESULT)Win32Error.ERROR_INVALID_DATATYPE;
            }

            return contact.DeleteArrayNode(nodeName, ContactValue.CGD_DEFAULT);
        }

		public static HRESULT DeleteLabels(INativeContactProperties contact, string nodeName)
        {
            Verify.IsNotNull(contact, "contact");

            return contact.DeleteLabels(nodeName, ContactValue.CGD_DEFAULT);
        }

		public static HRESULT DeleteProperty(INativeContactProperties contact, string propertyName)
        {
            Verify.IsNotNull(contact, "contact");
            Verify.IsNotNull(propertyName, "propertyName");

            // COM APIs don't check for this.  DeleteArrayNode should be used in this case.
            if (propertyName.EndsWith("]"))
            {
                return (HRESULT)Win32Error.ERROR_INVALID_DATATYPE;
            }

            return contact.DeleteProperty(propertyName, ContactValue.CGD_DEFAULT);
        }

        // There's a bug in Windows Contacts that simple extension array nodess return S_OK
        // instead of S_FALSE.  This function happens to behave correctly anyways.
		public static bool DoesPropertyExist(INativeContactProperties contact, string propertyName)
        {
            Verify.IsNotNull(contact, "contact");

            if (string.IsNullOrEmpty(propertyName))
            {
                return false;
            }

            string dummy;
            HRESULT hr = GetString(contact, propertyName, false, out dummy);
            if (HRESULT.S_FALSE == hr)
            {
                // S_FALSE usually implies a deleted property,
                // but if it's an array node then it's present.
                return ']' == propertyName[propertyName.Length - 1];
            }
            if ((HRESULT)Win32Error.ERROR_PATH_NOT_FOUND == hr)
            {
                return false;
            }
            // Other errors are unexpected.
            hr.ThrowIfFailed("Error querying the property");
            return true;
        }

        // Ideally the Environment class should be able to do this, but Contacts in Vista
        // is newer than the last rev of these .Net APIs.  Maybe next time...
        public static string GetContactsFolder()
        {
            IntPtr ptr = IntPtr.Zero;

            try
            {
                const int KF_FLAG_CREATE = 0x00008000;
                Guid FOLDERID_Contacts = new Guid("56784854-C6CB-462b-8169-88E350ACB882");
                SHGetKnownFolderPath(ref FOLDERID_Contacts, KF_FLAG_CREATE, IntPtr.Zero, out ptr);
                return Marshal.PtrToStringUni(ptr);
            }
			catch
			{
				string s = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "\\Contacts";
				if (!Directory.Exists(s))
					Directory.CreateDirectory(s);

				return s;
			}
			finally
            {
                Utility.SafeCoTaskMemFree(ref ptr);
            }
        }

		public static HRESULT GetBinary(INativeContactProperties contact, string propertyName, bool ignoreDeletes, out string binaryType, out Stream binary)
        {
            binaryType = null;
            binary = null;
            Verify.IsNotNull(contact, "contact");
            Verify.IsNotNull(propertyName, "propertyName");

            HRESULT hr = HRESULT.S_OK;
            StringBuilder sb = new StringBuilder((int)Win32Value.MAX_PATH);
            uint cch;
            IStream stm = null;

            try
            {
                hr = contact.GetBinary(propertyName, ContactValue.CGD_DEFAULT, sb, (uint)sb.Capacity, out cch, out stm);
                if (ignoreDeletes && HRESULT.S_FALSE == hr)
                {
                    hr = (HRESULT)Win32Error.ERROR_PATH_NOT_FOUND;
                }
                // If we didn't have enough space for the binaryType the first time through, try the bigger size.
                if ((HRESULT)Win32Error.ERROR_INSUFFICIENT_BUFFER == hr)
                {
                    Assert.IsNull(stm);
                    sb.EnsureCapacity((int)cch);
                    hr = contact.GetBinary(propertyName, ContactValue.CGD_DEFAULT, sb, (uint)sb.Capacity, out cch, out stm);
                    // GetBinary shouldn't return ERROR_INSUFFICIENT_BUFFER if it's going to subsequently return S_FALSE.
                    Assert.AreNotEqual(HRESULT.S_FALSE, hr);
                    // If this failed a second time, it shouldn't be because of an insufficient buffer.
                    Assert.Implies(hr.Failed(), (HRESULT)Win32Error.ERROR_INSUFFICIENT_BUFFER != hr);
                }

                if (HRESULT.S_OK == hr)
                {
                    binary = new ComStream(ref stm);
                    binaryType = sb.ToString();
                }
            }
            finally
            {
                Utility.SafeRelease(ref stm);
            }

            return hr;
        }

		public static HRESULT GetDate(INativeContactProperties contact, string propertyName, bool ignoreDeletes, out DateTime value)
        {
            value = default(DateTime);
            Verify.IsNotNull(contact, "contact");
            Verify.IsNotNull(propertyName, "propertyName");

            FILETIME ft;
            HRESULT hr = contact.GetDate(propertyName, ContactValue.CGD_DEFAULT, out ft);
            // If the caller doesn't care about deleted properties, convert the error code.
            if (ignoreDeletes && HRESULT.S_FALSE == hr)
            {
                hr = (HRESULT)Win32Error.ERROR_PATH_NOT_FOUND;
            }

            if (HRESULT.S_OK == hr)
            {
                value = ContactUtil.DateTimeFromFILETIME(ft);
            }

            return hr;
        }

        /// <summary>
        /// Tries to parse the index out of a property name that might represent an array node.
        /// </summary>
        /// <param name="propertyName">The array node property name that contains the index to parse.</param>
        /// <returns>The zero-based parsed index if this appears to be an array node.  Otherwise returns -1.</returns>
        public static int GetIndexFromNode(string propertyName)
        {
            int openBraceIndex = propertyName.LastIndexOf('[');
            int closeBraceIndex = propertyName.LastIndexOf(']');

            if (-1 == openBraceIndex || closeBraceIndex != propertyName.Length - 1)
            {
                return -1;
            }

            uint i;
            // Shouldn't accept negative values here.
            if (!UInt32.TryParse(propertyName.Substring(openBraceIndex + 1, closeBraceIndex - openBraceIndex - 1), out i))
            {
                return -1;
            }

            --i;
            if (i > Int32.MaxValue)
            {
                return -1;
            }

            return (int)i;
        }

        public static HRESULT GetID(IContact contact, out string path)
        {
            path = null;
            Verify.IsNotNull(contact, "contact");

            HRESULT hr = HRESULT.S_OK;
            StringBuilder sb = new StringBuilder((int)Win32Value.MAX_PATH);
            uint cch;

            hr = contact.GetContactID(sb, (uint)sb.Capacity, out cch);

            // If we didn't have enough space for the node the first time through, try the bigger size.
            if ((HRESULT)Win32Error.ERROR_INSUFFICIENT_BUFFER == hr)
            {
                sb.EnsureCapacity((int)cch);
                hr = contact.GetContactID(sb, (uint)sb.Capacity, out cch);

                // If this failed a second time, it shouldn't be because of an insufficient buffer.
                Assert.Implies(hr.Failed(), (HRESULT)Win32Error.ERROR_INSUFFICIENT_BUFFER != hr);
            }

            if (hr.Succeeded())
            {
                path = sb.ToString();
            }

            return hr;
        }

		public static HRESULT GetLabeledNode(INativeContactProperties contact, string collection, string[] labels, out string labeledNode)
        {
            labeledNode = null;
            Verify.IsNotNull(contact, "contact");
            Verify.IsNotNull(collection, "collection");

            if (null == labels)
            {
                labels = new string[0];
            }

            // Make a copy of the label set.
            // We're going to take two passes while trying to find the labeled value.
            // One has the Preferred label, the second doesn't.
            string[] preferredLabels = new string[labels.Length + 1];
            labels.CopyTo(preferredLabels, 0);
            preferredLabels[labels.Length] = PropertyLabels.Preferred;

            HRESULT hr = HRESULT.S_OK;
            IContactPropertyCollection propertyCollection = null;

            try
            {
                hr = GetPropertyCollection(contact, collection, preferredLabels, false, out propertyCollection);
                if (hr.Succeeded())
                {
                    // If a node satisfies this constraint, use it.
                    hr = propertyCollection.Next();
                    if (HRESULT.S_FALSE == hr)
                    {
                        // Otherwise, try it again without the extra "Preferred" label.
                        Utility.SafeRelease(ref propertyCollection);
                        hr = GetPropertyCollection(contact, collection, labels, false, out propertyCollection);
                        if (hr.Succeeded())
                        {
                            // Does an array node exist with these labels?
                            hr = propertyCollection.Next();
                            // There's nothing left to fall back on.  S_FALSE implies this property doesn't exist.
                            if (HRESULT.S_FALSE == hr)
                            {
                                hr = (HRESULT)Win32Error.ERROR_PATH_NOT_FOUND;
                            }
                        }
                    }
                }

                if (hr.Succeeded())
                {
                    hr = ContactUtil.GetPropertyName(propertyCollection, out labeledNode);
                }
            }
            finally
            {
                Utility.SafeRelease(ref propertyCollection);
            }

            return hr;
        }

		public static HRESULT GetLabels(INativeContactProperties contact, string arrayNode, out List<string> labels)
        {
            HRESULT hr = HRESULT.S_OK;
            labels = null;

            Verify.IsNotNull(contact, "contact");

            using (MarshalableDoubleNullString marshalable = new MarshalableDoubleNullString(Win32Value.MAX_PATH))
            {
                uint cch;
                hr = contact.GetLabels(arrayNode, ContactValue.CGD_DEFAULT, marshalable.MarshaledString, marshalable.Capacity, out cch);
                // If we didn't have enough space for the node the first time through, try the bigger size.
                if ((HRESULT)Win32Error.ERROR_INSUFFICIENT_BUFFER == hr)
                {
                    // Reallocate to the size returned by the last GetLabels call.
                    marshalable.Realloc(cch);

                    hr = contact.GetLabels(arrayNode, ContactValue.CGD_DEFAULT, marshalable.MarshaledString, marshalable.Capacity, out cch);
                    // If this failed a second time, it shouldn't be because of an insufficient buffer.
                    Assert.Implies(hr.Failed(), (HRESULT)Win32Error.ERROR_INSUFFICIENT_BUFFER != hr);
                }

                if (hr.Succeeded())
                {
                    labels = marshalable.ParsedStrings;
                }
            }

            return hr;
        }

        public static HRESULT GetPath(IContact contact, out string path)
        {
            path = null;
            Verify.IsNotNull(contact, "contact");

            HRESULT hr = HRESULT.S_OK;
            StringBuilder sb = new StringBuilder((int)Win32Value.MAX_PATH);
            uint cch;

            hr = contact.GetPath(sb, (uint)sb.Capacity, out cch);

            // If we didn't have enough space for the node the first time through, try the bigger size.
            if ((HRESULT)Win32Error.ERROR_INSUFFICIENT_BUFFER == hr)
            {
                sb.EnsureCapacity((int)cch);
                hr = contact.GetPath(sb, (uint)sb.Capacity, out cch);

                // If this failed a second time, it shouldn't be because of an insufficient buffer.
                Assert.Implies(hr.Failed(), (HRESULT)Win32Error.ERROR_INSUFFICIENT_BUFFER != hr);
            }

            if (hr.Succeeded())
            {
                path = sb.ToString();
            }

            return hr;
        }

		public static HRESULT GetPropertyCollection(INativeContactProperties contact, string collection, string[] labels, bool anyLabelMatches, out IContactPropertyCollection propertyCollection)
        {
            propertyCollection = null;
            Verify.IsNotNull(contact, "contact");

            uint fAnyLabelMatches = anyLabelMatches ? Win32Value.TRUE : Win32Value.FALSE;

            using (MarshalableLabelCollection mlc = new MarshalableLabelCollection(labels))
            {
                return contact.GetPropertyCollection(out propertyCollection, ContactValue.CGD_DEFAULT, collection, mlc.Count, mlc.MarshaledLabels, fAnyLabelMatches);
            }
        }

        public static HRESULT GetPropertyModificationDate(IContactPropertyCollection propertyCollection, out DateTime date)
        {
            date = default(DateTime);
            Verify.IsNotNull(propertyCollection, "propertyCollection");

            FILETIME ft;
            HRESULT hr = propertyCollection.GetPropertyModificationDate(out ft);
            if (hr.Succeeded())
            {
                date = DateTimeFromFILETIME(ft);
            }
            return hr;
        }

		
        public static HRESULT GetPropertyID(IContactPropertyCollection propertyCollection, out Guid guid)
        {
            guid = default(Guid);
            Verify.IsNotNull(propertyCollection, "propertyCollection");

            // Allocate a StringBuilder big enough to hold a guid.  No retry logic here.
            StringBuilder sb = new StringBuilder(100);
            uint cch;
            HRESULT hr = propertyCollection.GetPropertyArrayElementID(sb, (uint)sb.Capacity, out cch);
            // This should never fail for the reason of insufficient buffer.
            Assert.AreNotEqual<HRESULT>(Win32Error.ERROR_INSUFFICIENT_BUFFER, hr);
            
            // Contacts returns S_OK when this isn't an array-node, but it leaves the string blank.
            if (hr.Succeeded())
            {
                if (sb.Length == 0)
                {
                    uint dwType;
					GetPropertyType(propertyCollection, out dwType);
                    Assert.AreNotEqual(dwType, ContactValue.CGD_ARRAY_NODE);
                    hr = Win32Error.ERROR_INVALID_DATATYPE;
                }
                else
                {
                    guid = new Guid(sb.ToString());
                }
            }

            return hr;
        }

        public static HRESULT GetPropertyName(IContactPropertyCollection propertyCollection, out string name)
        {
            name = null;
            Verify.IsNotNull(propertyCollection, "propertyCollection");

            StringBuilder sb = new StringBuilder((int)Win32Value.MAX_PATH);
            uint cch;
            HRESULT hr = propertyCollection.GetPropertyName(sb, (uint)sb.Capacity, out cch);
            // If we didn't have enough space for the node the first time through, try the bigger size.
            if ((HRESULT)Win32Error.ERROR_INSUFFICIENT_BUFFER == hr)
            {
                sb.EnsureCapacity((int)cch);
                hr = propertyCollection.GetPropertyName(sb, (uint)sb.Capacity, out cch);

                // If this failed a second time, it shouldn't be because of an insufficient buffer.
                Assert.Implies(hr.Failed(), (HRESULT)Win32Error.ERROR_INSUFFICIENT_BUFFER != hr);
            }

            if (hr.Succeeded())
            {
                name = sb.ToString();
            }

            return hr;
        }

        public static HRESULT GetPropertyType(IContactPropertyCollection propertyCollection, out uint type)
        {
            type = ContactValue.CGD_UNKNOWN_PROPERTY;
            Verify.IsNotNull(propertyCollection, "propertyCollection");

            return propertyCollection.GetPropertyType(out type);
        }

        public static HRESULT GetPropertyVersion(IContactPropertyCollection propertyCollection, out int version)
        {
            version = -1;
            Verify.IsNotNull(propertyCollection, "propertyCollection");

            uint dwVersion;
            HRESULT hr = propertyCollection.GetPropertyVersion(out dwVersion);
            if (hr.Succeeded())
            {
                version = (int)dwVersion;
            }

            return hr;
        }

		public static HRESULT GetString(INativeContactProperties contact, string propertyName, bool ignoreDeletes, out string value)
        {
            value = null;
            Verify.IsNotNull(contact, "contact");
            Verify.IsNotNull(propertyName, "propertyName");

            uint cch;
            StringBuilder sb = new StringBuilder((int)Win32Value.MAX_PATH);
            HRESULT hr = contact.GetString(propertyName, ContactValue.CGD_DEFAULT, sb, (uint)sb.Capacity, out cch);
            // If the caller doesn't care about deleted properties, convert the error code.
            if (ignoreDeletes && HRESULT.S_FALSE == hr)
            {
                hr = (HRESULT)Win32Error.ERROR_PATH_NOT_FOUND;
            }
            // If we didn't have enough space for the value the first time through, try the bigger size.
            if ((HRESULT)Win32Error.ERROR_INSUFFICIENT_BUFFER == hr)
            {
                sb.EnsureCapacity((int)cch);
                hr = contact.GetString(propertyName, ContactValue.CGD_DEFAULT, sb, (uint)sb.Capacity, out cch);

                // If this failed a second time, it shouldn't be because of an insufficient buffer.
                Assert.Implies(hr.Failed(), (HRESULT)Win32Error.ERROR_INSUFFICIENT_BUFFER != hr);
            }

            if (HRESULT.S_OK == hr)
            {
                value = sb.ToString();
            }

            return hr;
        }

        /// <summary>
        /// Does the given string represent a legal array-node property name?
        /// </summary>
        /// <param name="propertyName">The string to check.</param>
        /// <returns>Returns whether the string is a legal array node.</returns>
        public static bool IsPropertyValidNode(string propertyName)
        {
            return -1 != GetIndexFromNode(propertyName);
        }

        /// <summary>
        /// Tokenizes an unmanaged WCHAR array of multiple embedded strings into a List{string}
        /// </summary>
        /// <param name="doubleNullString">
        /// An IntPtr that points to an unmanaged WCHAR[] containing multiple strings.
        /// Each string in the parameter is terminated by a null character.  The parameter
        /// itself is terminated by a pair of null characters.  If the parameter begins with
        /// a null character, it doesn't necessarily need a second terminating null.
        /// </param>
        /// <returns>
        /// A list of the embedded strings in the doubleNullString parameter.
        /// If there are no strings in the parameter, then an empty string is returned.
        /// </returns>
        /// <exception cref="System.ArgumentNullException" >
        /// doubleNullString must point at valid memory.
        /// </exception>
        public static List<string> ParseDoubleNullString(IntPtr doubleNullString)
        {
            if (IntPtr.Zero == doubleNullString)
            {
                throw new ArgumentNullException("doubleNullString");
            }

            List<string> results = new List<string>();

            IntPtr currentPtr = doubleNullString;
            while (true)
            {
                string fragment = Marshal.PtrToStringUni(currentPtr);
                Assert.IsNotNull(fragment);

                // This might catch even when currentPtr == doubleNullString.
                // If the parameter is empty, then this function doesn't care about a second null.
                if (fragment.Length == 0)
                {
                    break;
                }

                results.Add(fragment);
                currentPtr = (IntPtr)((int)currentPtr + (fragment.Length + 1) * Win32Value.sizeof_WCHAR);
            }

            return results;
        }

        /// <summary>
        /// Utility to set a binary property on an IContactProperties.
        /// </summary>
        /// <param name="contact">The IContactProperties to set the value on.</param>
        /// <param name="propertyName">The property to set.</param>
        /// <param name="binary">The value to set to the property.</param>
        /// <param name="binaryType">The mime-type of the value being applied.</param>
        /// <returns>HRESULT.</returns>
        /// <remarks>
        /// This is a thin wrapper over the COM IContactProperties::SetBinary to make it more easily consumable
        /// in .Net.  Behavior and returned error codes should be similar to the native version.
        /// </remarks>
        public static HRESULT SetBinary(INativeContactProperties contact, string propertyName, string binaryType, Stream binary)
        {
            Verify.IsNotNull(contact, "contact");
            Verify.IsNotNull(propertyName, "propertyName");

            using (ManagedIStream mstream = new ManagedIStream(binary))
            {
                mstream.Seek(0, (int)SeekOrigin.Begin, IntPtr.Zero);
                return contact.SetBinary(propertyName, ContactValue.CGD_DEFAULT, binaryType, mstream);
            }
        }

        /// <summary>
        /// Utility to set a date property on an IContactProperties.
        /// </summary>
        /// <param name="contact">The IContactProperties to set the value on.</param>
        /// <param name="propertyName">The property to set.</param>
        /// <param name="value">The date value to set to the property.</param>
        /// <returns>HRESULT.</returns>
        /// <remarks>
        /// This is a thin wrapper over the COM IContactProperties::SetDate to make it more easily consumable
        /// in .Net.  Behavior and returned error codes should be similar to the native version.
        /// </remarks>
		public static HRESULT SetDate(INativeContactProperties contact, string propertyName, DateTime value)
        {
            Verify.IsNotNull(contact, "contact");
            Verify.IsNotNull(propertyName, "propertyName");

            // If the caller hasn't explicitly set the kind then assume it's UTC
            // so it will be written as read to the Contact.  
            if (value.Kind != DateTimeKind.Local)
            {
                value = new DateTime(value.Ticks, DateTimeKind.Utc);
            }

            long longFiletime = value.ToFileTime();

            FILETIME ft = new FILETIME();
            ft.dwLowDateTime = (Int32)(value.ToFileTime());
            ft.dwHighDateTime = (Int32)(value.ToFileTime() >> 32);

            return contact.SetDate(propertyName, ContactValue.CGD_DEFAULT, ft);
        }

        /// <summary>
        /// Utility to augment the label set on a preexisting array node in an IContactProperties.
        /// </summary>
        /// <param name="contact">The IContactProperties where the labels are to be set.</param>
        /// <param name="arrayNode">The array node to apply the labels to.</param>
        /// <param name="labels">The labels to add to the array node.</param>
        /// <returns>HRESULT.</returns>
        /// <remarks>
        /// This is a thin wrapper over the COM IContactProperties::SetLabels to make it more easily consumable
        /// in .Net.  Behavior and returned error codes should be similar to the native version.
        /// </remarks>
        public static HRESULT SetLabels(INativeContactProperties contact, string arrayNode, ICollection<string> labels)
        {
            Verify.IsNotNull(contact, "contact");

            HRESULT hr = HRESULT.S_OK;

            using (MarshalableLabelCollection marshalable = new MarshalableLabelCollection(labels))
            {
                hr = contact.SetLabels(arrayNode, ContactValue.CGD_DEFAULT, marshalable.Count, marshalable.MarshaledLabels);
            }

            return hr;
        }

        /// <summary>
        /// Utility to set a string property on an IContactProperties.
        /// </summary>
        /// <param name="contact">The IContactProperties to set the value on.</param>
        /// <param name="propertyName">The property to set.</param>
        /// <param name="value">The value to set to the property.</param>
        /// <returns>HRESULT.</returns>
        /// <remarks>
        /// This is a thin wrapper over the COM IContactProperties::SetString to make it more easily consumable
        /// in .Net.  Behavior and returned error codes should be similar to the native version.
        /// </remarks>
		public static HRESULT SetString(INativeContactProperties contact, string propertyName, string value)
        {
            Verify.IsNotNull(contact, "contact");
            Verify.IsNotNull(propertyName, "propertyName");

            return contact.SetString(propertyName, ContactValue.CGD_DEFAULT, value);
        }

        /// <summary>
        /// Utility to parse tokens out of a runtime ContactId.
        /// </summary>
        /// <param name="contactId">The runtime ContactId to parse.</param>
        /// <param name="token">The token to search for in the Id.</param>
        /// <returns>
        /// The value of the token in the id, if the token exists in the id.
        /// If the token is missing then null is returned.
        /// </returns>
        public static string TokenizeContactId(string contactId, ContactIdToken token)
        {
            Verify.IsNotNull(contactId, "contactId");
            if (!_tokenMap.ContainsKey(token))
            {
                throw new ArgumentException("Invalid token.", "token");
            }

            Dictionary<string, string> tokens = TokenizeId(contactId);
            string tokenValue;
            if (tokens.TryGetValue(_tokenMap[token], out tokenValue))
            {
                return tokenValue;
            }
            return null;
        }

        public static Dictionary<string,string> TokenizeId(string id)
        {
            if (id != null)
            {
                id = id.Trim();
            }
            Verify.IsNeitherNullNorEmpty(id, "id");
            Dictionary<string, string> retMap = new Dictionary<string, string>();
            string[] splitArray = id.Split('\"');
            // Expect a trailing empty string given the way the id is split.
            if (0 == splitArray.Length % 2 || 0 != splitArray[splitArray.Length-1].Length)
            {
                throw new FormatException("Improperly formatted Id string.  The value for a token isn't properly closed.");
            }
            for (int i = 0; i < splitArray.Length-1; i += 2)
            {
                // Remove whitespace that precedes the token.
                retMap.Add(splitArray[i].Trim(), splitArray[i + 1]);
            }
            return retMap;
        }

    }
}
