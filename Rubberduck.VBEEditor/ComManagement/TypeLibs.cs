﻿using System;
using System.Runtime.InteropServices;
using System.Collections;
using System.Collections.Generic;
using Rubberduck.VBEditor.SafeComWrappers.Abstract;
using Rubberduck.VBEditor.ComManagement.TypeLibsAbstract;
using ComTypes = System.Runtime.InteropServices.ComTypes;
using Reflection = System.Reflection;

// USAGE GUIDE:   see class VBETypeLibsAPI for demonstrations of usage.
//
// The root object for exposure of the type libraries is TypeLibsAccessor_VBE.  It takes the VBE in its construtor.
// The main wrappers (TypeLibWrapper_VBE and TypeInfoWrapper_VBE) can be used as regular ITypeLib and ITypeInfo objects through casting.
//
// THIS IS A WORK IN PROGRESS.  ERROR HANDLING NEEDS WORK, AS DOES A FEW OF THE HELPER ROUTINES.
//
// WARNING: when using TypeLibsAccessor_VBE directly, do not cache it  
//   The type library is LIVE information, so consider it a snapshot at that very moment when you are dealing with it
//   Make sure you call TypeLibsAccessor_VBE.Dispose() as soon as you have done what you need to do with it.
//   Once control returns back to the VBE, you must assume that all the ITypeLib/ITypeInfo pointers are now invalid.
//
// CURRENT LIMITATIONS:
// At the moment, enums and UDTs are not exposed through the type libraries
// Constants names are not available

// IMPLEMENTATION DETAILS:
// There are two significant bugs in the VBE typeinfos implementations that we have to work around.
// 1)  Some implementations of ITypeInfo provided by the VBE will crash with an AV if you call IProvideClassInfo::GetClassInfo on them.
//      And guess what method the CLR calls on all COM interop objects when creating a RCW?  You guessed it.
//      So, we use an aggregation object, plus ITypeInfo and ITypeLib wrappers to circumvent this VBE bug.
//
// 2)  The ITypeInfo for base classes of UserForms crash with an AV if you call ITypeInfo::GetDocumentation(MEMBERID_NIL) to get the type name
//     We've got to remember that the VBE didn't ever intend for us to get hold of these objects, so there will be little bugs.
//     This bug is also resolved in the provided wrappers.
//
// All the extended functionality is exposed through the wrappers.

namespace Rubberduck.VBEditor.ComManagement.TypeLibs
{
    public class StructHelper
    {
        public static T ReadStructure<T>(object comObj)
        {
            // Reads a COM object as a structure to copy its internal fields
            var referencesPtr = Marshal.GetIUnknownForObject(comObj);
            var retVal = StructHelper.ReadStructure<T>(referencesPtr);
            Marshal.Release(referencesPtr);
            return retVal;
        }

        public static T ReadStructure<T>(IntPtr memAddress)
        {
            if (memAddress == IntPtr.Zero) return default(T);
            return (T)Marshal.PtrToStructure(memAddress, typeof(T));
        }

        public static T ReadStructureSafe<T>(IntPtr memAddress)
        {
            if (memAddress == IntPtr.Zero) return default(T);

            // FIXME add memory address validation here, using VirtualQueryEx
            return (T)Marshal.PtrToStructure(memAddress, typeof(T));
        }
    }

    // AggregateSingleInterface is used to ensure that a wrapped COM object only responds to a specific interface
    // In particular, we don't want them to respond to IProvideClassInfo, which is broken in the VBE for some ITypeInfo implementations 
    public class AggregateSingleInterface<T> : ICustomQueryInterface, IDisposable
        where T : class
    {
        private IntPtr _outerObject;

        public AggregateSingleInterface(IntPtr outerObject)
        {
            _outerObject = outerObject;
            Marshal.AddRef(_outerObject);
        }

        private bool _isDisposed;
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Marshal.Release(_outerObject);
        }

        public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr ppv)
        {
            ppv = IntPtr.Zero;
            if ((!_isDisposed) && (iid == typeof(T).GUID))       // no need to offer IID_IUnknown here, as it is handled by the aggregation object
            {
                ppv = _outerObject;
                Marshal.AddRef(_outerObject);
                return CustomQueryInterfaceResult.Handled;
            }
            return CustomQueryInterfaceResult.Failed;
        }
    }

    // FIXME there's probably some better builtin c# class for this
    public class DisposableList<T> : List<T>, IDisposable
        where T : class
    {
        public void Dispose() => ((IDisposable)this).Dispose();

        private bool _isDisposed;
        void IDisposable.Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            foreach (var element in this)
            {
                ((IDisposable)element).Dispose();
            }
        }
    }

    // A wrapper for ITypeInfo provided by VBE, allowing safe managed consumption, plus adds StdModExecute functionality
    public class TypeInfoWrapper_VBE : ComTypes.ITypeInfo, IDisposable
    {
        private DisposableList<TypeInfoWrapper_VBE> _typeInfosWrapped;
        private TypeLibWrapper_VBE _containerTypeLib;
        private int _containerTypeLibIndex;
        private AggregateSingleInterface<ComTypes.ITypeInfo> _typeInfoAggregatorObj;
        private bool _isUserFormBaseClass = false;
        private ComTypes.TYPEATTR _cachedAttributes;

        public readonly string Name;
        public readonly string DocString;
        public readonly int HelpContext;
        public readonly string HelpFile;

        private ComTypes.ITypeInfo _wrappedObject;
        private ITypeInfo_VBE _ITypeInfoAlt
            { get => ((ITypeInfo_VBE)_wrappedObject); }

        public TypeInfoWrapper_VBE(IntPtr rawObjectPtr, int? parentUserFormUniqueId = null)
        {
            _typeInfoAggregatorObj = new AggregateSingleInterface<ComTypes.ITypeInfo>(rawObjectPtr);
            var aggObjPtr = Marshal.CreateAggregatedObject(rawObjectPtr, _typeInfoAggregatorObj);
            _wrappedObject = (ComTypes.ITypeInfo)Marshal.GetObjectForIUnknown(aggObjPtr);        // when this CCW object gets released, it will free the aggObjInner (well, after GC)
            Marshal.Release(aggObjPtr);         // _wrappedObject holds a reference to this now

            IntPtr typeAttrPtr = IntPtr.Zero;
            GetTypeAttr(out typeAttrPtr);
            _cachedAttributes = StructHelper.ReadStructure<ComTypes.TYPEATTR>(typeAttrPtr);
            ReleaseTypeAttr(typeAttrPtr);      // don't need to keep a hold of it, as _cachedAttributes is a copy

            // cache the container type library if it is available
            try
            {
                // We have to wrap the ITypeLib returned by GetContainingTypeLib
                // so we cast to our ITypeInfo_VBE interface in order to work with the raw IntPtrs
                IntPtr typeLibPtr = IntPtr.Zero;
                _ITypeInfoAlt.GetContainingTypeLib(out typeLibPtr, out _containerTypeLibIndex);
                _containerTypeLib = new TypeLibWrapper_VBE(typeLibPtr);  // takes ownership of the COM reference
            }
            catch (Exception e)
            {
                // it is acceptable for a type to not have a container, as types can be runtime generated.
            }
            
            // base classes of UserForms cause an access violation on calling GetDocumentation(MEMBERID_NIL)
            // so we have to detect UserForm parents, and ensure GetDocumentation(MEMBERID_NIL) never gets through
            if (parentUserFormUniqueId.HasValue)
            {
                Name = "_UserFormBase{unnamed}#" + parentUserFormUniqueId;
            }
            else
            {
                // cache the type Name etc
                _wrappedObject.GetDocumentation((int)TypeLibConsts.MEMBERID_NIL, out Name, out DocString, out HelpContext, out HelpFile);

                DetectUserFormClass();
            }
        }

        private bool IsRuntimeGenerated() => _containerTypeLib == null;

        private void DetectUserFormClass()
        {
            // Determine if this is a UserForm base class, that requires special handling to workaround a VBE bug in its implemented classes
            // the guids are dynamic, so we can't use them for detection.
            if ((_cachedAttributes.typekind == ComTypes.TYPEKIND.TKIND_COCLASS) &&
                    IsRuntimeGenerated() &&
                    (_cachedAttributes.cImplTypes == 2) && (Name == "Form"))
            {
                // we can be 99.9999% sure it IS the runtime generated UserForm base class
                _isUserFormBaseClass = true;
            }
        }

        private bool _isDisposed;
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (_typeInfosWrapped != null) _typeInfosWrapped.Dispose();
            if (_containerTypeLib != null) _containerTypeLib.Dispose();
            Marshal.ReleaseComObject(_wrappedObject);
            _typeInfoAggregatorObj.Dispose();
        }

        // We have to wrap the ITypeInfo returned by GetRefTypeInfo
        // so we cast to our ITypeInfo_VBE interface in order to work with the raw IntPtr for aggregation
        public void /* ITypeInfo:: */ GetRefTypeInfo(int hRef, out ComTypes.ITypeInfo ppTI)
        {
            IntPtr typeInfoPtr = IntPtr.Zero;
            _ITypeInfoAlt.GetRefTypeInfo(hRef, out typeInfoPtr);
            var outVal = new TypeInfoWrapper_VBE(typeInfoPtr, _isUserFormBaseClass ? (int?)hRef : null); // takes ownership of the COM reference
            ppTI = outVal;

            if (_typeInfosWrapped == null) _typeInfosWrapped = new DisposableList<TypeInfoWrapper_VBE>();
            _typeInfosWrapped.Add(outVal);
        }

        public void /* ITypeInfo:: */ GetContainingTypeLib(out ComTypes.ITypeLib ppTLB, out int pIndex)
        {
            ppTLB = _containerTypeLib;
            pIndex = _containerTypeLibIndex;
        }

        // All other members just pass through to the wrappedObject
        public void /* ITypeInfo:: */ GetTypeAttr(out IntPtr ppTypeAttr)
            => _wrappedObject.GetTypeAttr(out ppTypeAttr);
        public void /* ITypeInfo:: */ GetTypeComp(out ComTypes.ITypeComp ppTComp)
            => _wrappedObject.GetTypeComp(out ppTComp);
        public void /* ITypeInfo:: */ GetFuncDesc(int index, out IntPtr ppFuncDesc)
            => _wrappedObject.GetFuncDesc(index, out ppFuncDesc);
        public void /* ITypeInfo:: */ GetVarDesc(int index, out IntPtr ppVarDesc)
            => _wrappedObject.GetVarDesc(index, out ppVarDesc);
        public void /* ITypeInfo:: */ GetNames(int memid, string[] rgBstrNames, int cMaxNames, out int pcNames)
            => _wrappedObject.GetNames(memid, rgBstrNames, cMaxNames, out pcNames);
        public void /* ITypeInfo:: */ GetRefTypeOfImplType(int index, out int href)
            => _wrappedObject.GetRefTypeOfImplType(index, out href);
        public void /* ITypeInfo:: */ GetImplTypeFlags(int index, out ComTypes.IMPLTYPEFLAGS pImplTypeFlags)
            => _wrappedObject.GetImplTypeFlags(index, out pImplTypeFlags);
        public void /* ITypeInfo:: */ GetIDsOfNames(string[] rgszNames, int cNames, int[] pMemId)
            => _wrappedObject.GetIDsOfNames(rgszNames, cNames, pMemId);
        public void /* ITypeInfo:: */ Invoke(object pvInstance, int memid, short wFlags, ref ComTypes.DISPPARAMS pDispParams, IntPtr pVarResult, IntPtr pExcepInfo, out int puArgErr)
            => _wrappedObject.Invoke(pvInstance, memid, wFlags, ref pDispParams, pVarResult, pExcepInfo, out puArgErr);
        public void /* ITypeInfo:: */ GetDocumentation(int index, out string strName, out string strDocString, out int dwHelpContext, out string strHelpFile)
        {
            if (index == (int)TypeLibConsts.MEMBERID_NIL)
            {
                // return the cached information here, to workaround the VBE bug for unnamed UserForm base classes causing an access violation
                strName = Name;
                strDocString = DocString;
                dwHelpContext = HelpContext;
                strHelpFile = HelpFile;
            }
            else
            {
                _wrappedObject.GetDocumentation(index, out strName, out strDocString, out dwHelpContext, out strHelpFile);
            }
        }
        public void /* ITypeInfo:: */ GetDllEntry(int memid, ComTypes.INVOKEKIND invKind, IntPtr pBstrDllName, IntPtr pBstrName, IntPtr pwOrdinal)
            => _wrappedObject.GetDllEntry(memid, invKind, pBstrDllName, pBstrName, pwOrdinal);
        public void /* ITypeInfo:: */ AddressOfMember(int memid, ComTypes.INVOKEKIND invKind, out IntPtr ppv)
            => _wrappedObject.AddressOfMember(memid, invKind, out ppv);
        public void /* ITypeInfo:: */ CreateInstance(object pUnkOuter, ref Guid riid, out object ppvObj)
            => _wrappedObject.CreateInstance(pUnkOuter, riid, out ppvObj);
        public void /* ITypeInfo:: */ GetMops(int memid, out string pBstrMops)
            => _wrappedObject.GetMops(memid, out pBstrMops);
        public void /* ITypeInfo:: */ ReleaseTypeAttr(IntPtr pTypeAttr)
            => _wrappedObject.ReleaseTypeAttr(pTypeAttr);
        public void /* ITypeInfo:: */ ReleaseFuncDesc(IntPtr pFuncDesc)
            => _wrappedObject.ReleaseFuncDesc(pFuncDesc);
        public void /* ITypeInfo:: */ ReleaseVarDesc(IntPtr pVarDesc)
            => _wrappedObject.ReleaseVarDesc(pVarDesc);

        public IDispatch GetStdModInstance() => _ITypeInfoAlt.GetStdModInstance();
        public object StdModExecute(string name, Reflection.BindingFlags invokeAttr, object[] args = null)
        {
            var StaticModule = GetStdModInstance();
            var retVal = StaticModule.GetType().InvokeMember(name, invokeAttr, null, StaticModule, args);
            Marshal.ReleaseComObject(StaticModule);
            return retVal;
        }

        public TypeInfoWrapper_VBE GetImplementedTypeInfoByIndex(int implIndex)
        {
            ComTypes.ITypeInfo typeInfoImpl = null;
            int href = 0;
            GetRefTypeOfImplType(implIndex, out href);
            GetRefTypeInfo(href, out typeInfoImpl);
            return (TypeInfoWrapper_VBE)typeInfoImpl;
        }

        public bool DoesImplement(string interfaceName)
        {
            if (Name == interfaceName) return true;

            for (int implIndex = 0; implIndex < _cachedAttributes.cImplTypes; implIndex++)
            {
                using (var typeInfoImplEx = GetImplementedTypeInfoByIndex(implIndex))
                {
                    if (typeInfoImplEx.DoesImplement(interfaceName))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public bool DoesImplement(Guid interfaceIID)
        {
            if (_cachedAttributes.guid == interfaceIID) return true;

            for (int implIndex = 0; implIndex < _cachedAttributes.cImplTypes; implIndex++)
            {
                using (var typeInfoImplEx = GetImplementedTypeInfoByIndex(implIndex))
                {
                    if (typeInfoImplEx.DoesImplement(interfaceIID))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public TypeInfoWrapper_VBE GetImplementedTypeInfo(string searchTypeName)
        {
            for (int implIndex = 0; implIndex < _cachedAttributes.cImplTypes; implIndex++)
            {
                var typeInfoImplEx = GetImplementedTypeInfoByIndex(implIndex);
                if (typeInfoImplEx.Name == searchTypeName)
                {
                    return typeInfoImplEx;
                }
                typeInfoImplEx.Dispose();
            }

            throw new ArgumentException($"TypeLibWrapper_VBE::GetImplementedTypeInfo failed. '{searchTypeName}' module not found.");
        }

        // FIXME this needs work
        // Gets the control ITypeInfo by looking for the corresponding getter on the form interface and returning its retval type
        // Supports UserForms.  what about Access forms etc
        public TypeInfoWrapper_VBE GetControlType(string controlName)
        {
            for (int funcIndex = 0; funcIndex < _cachedAttributes.cFuncs; funcIndex++)
            {
                IntPtr funcDescPtr = IntPtr.Zero;
                GetFuncDesc(funcIndex, out funcDescPtr);
                var funcDesc = StructHelper.ReadStructure<ComTypes.FUNCDESC>(funcDescPtr);

                try
                {
                    var names = new string[1];
                    int cNames = 0;
                    GetNames(funcDesc.memid, names, names.Length, out cNames);

                    if (names[0] == controlName)
                    {
                        if (((funcDesc.invkind & ComTypes.INVOKEKIND.INVOKE_PROPERTYGET) != 0) && (funcDesc.cParams == 0))
                        {
                            if (funcDesc.elemdescFunc.tdesc.vt == 26)       // VT_PTR
                            {
                                var retValElement = StructHelper.ReadStructure<ComTypes.ELEMDESC>(funcDesc.elemdescFunc.tdesc.lpValue);
                                if (retValElement.tdesc.vt == 29)       // VT_USERDEFINED
                                {
                                    ComTypes.ITypeInfo referenceType;
                                    GetRefTypeInfo((int)retValElement.tdesc.lpValue, out referenceType);
                                    return (TypeInfoWrapper_VBE)referenceType;
                                }
                            }
                        }
                    }
                }
                finally
                {
                    ReleaseFuncDesc(funcDescPtr);
                }
            }

            throw new ArgumentException($"TypeInfoWrapper_VBE::GetControlType failed. '{controlName}' control not found.");
        }
    }

    // A wrapper for ITypeLib that exposes VBE ITypeInfos safely for managed consumption, plus adds ConditionalCompilationArguments property
    public class TypeLibWrapper_VBE : ComTypes.ITypeLib, IDisposable
    {
        private DisposableList<TypeInfoWrapper_VBE> _typeInfosWrapped;
        private ComTypes.ITypeLib _wrappedObject;

        public readonly string Name;
        public readonly string DocString;
        public readonly int HelpContext;
        public readonly string HelpFile;

        private ITypeLib_VBE _ITypeLibAlt
            { get => ((ITypeLib_VBE)_wrappedObject); }

        private IVBProjectEx_VBE _IVBProjectEx
            { get => ((IVBProjectEx_VBE)_wrappedObject); }

        public TypeLibWrapper_VBE(IntPtr rawObjectPtr)
        {
            _wrappedObject = (ComTypes.ITypeLib)Marshal.GetObjectForIUnknown(rawObjectPtr);
            Marshal.Release(rawObjectPtr);         // _wrappedObject holds a reference to this now

            _wrappedObject.GetDocumentation((int)TypeLibConsts.MEMBERID_NIL, out Name, out DocString, out HelpContext, out HelpFile);
        }

        private bool _isDisposed;
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (_typeInfosWrapped != null) _typeInfosWrapped.Dispose();
            Marshal.ReleaseComObject(_wrappedObject);
        }

        // We have to wrap the ITypeInfo returned by GetTypeInfo
        // so we cast to our ITypeLib_VBE interface in order to work with the raw IntPtr for aggregation
        public void /* ITypeLib:: */ GetTypeInfo(int index, out ComTypes.ITypeInfo ppTI)
        {
            IntPtr typeInfoPtr = IntPtr.Zero;
            _ITypeLibAlt.GetTypeInfo(index, out typeInfoPtr);
            var outVal = new TypeInfoWrapper_VBE(typeInfoPtr);
            ppTI = outVal;     // takes ownership of the COM reference

            if (_typeInfosWrapped == null) _typeInfosWrapped = new DisposableList<TypeInfoWrapper_VBE>();
            _typeInfosWrapped.Add(outVal);
        }

        // We have to wrap the ITypeInfo returned by GetTypeInfoOfGuid
        // so we cast to our ITypeLib_VBE interface in order to work with the raw IntPtr for aggregation
        public void /* ITypeLib:: */ GetTypeInfoOfGuid(ref Guid guid, out ComTypes.ITypeInfo ppTInfo)
        {
            IntPtr typeInfoPtr = IntPtr.Zero;
            _ITypeLibAlt.GetTypeInfoOfGuid(guid, out typeInfoPtr);
            var outVal = new TypeInfoWrapper_VBE(typeInfoPtr);  // takes ownership of the COM reference
            ppTInfo = outVal;

            if (_typeInfosWrapped == null) _typeInfosWrapped = new DisposableList<TypeInfoWrapper_VBE>();
            _typeInfosWrapped.Add(outVal);
        }

        // All other members just pass through to the wrappedObject
        public int /* ITypeLib:: */ GetTypeInfoCount()
            => _wrappedObject.GetTypeInfoCount();
        public void /* ITypeLib:: */ GetTypeInfoType(int index, out ComTypes.TYPEKIND pTKind)
            => _wrappedObject.GetTypeInfoType(index, out pTKind);
        public void /* ITypeLib:: */ GetLibAttr(out IntPtr ppTLibAttr)
            => _wrappedObject.GetLibAttr(out ppTLibAttr);
        public void /* ITypeLib:: */ GetTypeComp(out ComTypes.ITypeComp ppTComp)
            => _wrappedObject.GetTypeComp(out ppTComp);
        public void /* ITypeLib:: */ GetDocumentation(int index, out string strName, out string strDocString, out int dwHelpContext, out string strHelpFile)
            => _wrappedObject.GetDocumentation(index, out strName, out strDocString, out dwHelpContext, out strHelpFile);
        public bool /* ITypeLib:: */ IsName(string szNameBuf, int lHashVal)
            => _wrappedObject.IsName(szNameBuf, lHashVal);

        // FIXME need to wrap the elements of ITypeInfos returned in FindName here.  RD never calls ITypeInfo::FindName() though, so low priority.
        public void /* ITypeLib:: */ FindName(string szNameBuf, int lHashVal, ComTypes.ITypeInfo[] ppTInfo, int[] rgMemId, ref short pcFound)
            => _wrappedObject.FindName(szNameBuf, lHashVal, ppTInfo, rgMemId, pcFound);
        public void /* ITypeLib:: */ ReleaseTLibAttr(IntPtr pTLibAttr)
            => _wrappedObject.ReleaseTLibAttr(pTLibAttr);

        public string ConditionalCompilationArguments
        {
            get => _IVBProjectEx.get_ConditionalCompilationArgs();
            set => _IVBProjectEx.set_ConditionalCompilationArgs(value);
        }

        public TypeInfoWrapper_VBE FindTypeInfo(string searchTypeName)
        {
            int countOfTypes = GetTypeInfoCount();

            for (int typeIdx = 0; typeIdx < countOfTypes; typeIdx++)
            {
                ComTypes.ITypeInfo typeInfo;
                GetTypeInfo(typeIdx, out typeInfo);

                var typeInfoEx = (TypeInfoWrapper_VBE)typeInfo;
                if (typeInfoEx.Name == searchTypeName)
                {
                    return typeInfoEx;
                }

                typeInfoEx.Dispose();
            }

            throw new ArgumentException($"TypeLibWrapper_VBE::FindTypeInfo failed. '{searchTypeName}' module not found.");
        }
    }

    // class for iterating over the double linked list of ITypeLibs provided by the VBE
    public class TypeLibsIterator_VBE : IEnumerable<TypeLibWrapper_VBE>, IEnumerator<TypeLibWrapper_VBE>, IDisposable
    {
        private IntPtr _currentTypeLibPtr;
        private TypeLibObj_VBE _currentTypeLibStruct;
        private bool _isStart;

        public TypeLibsIterator_VBE(IntPtr typeLibPtr)
        {
            _currentTypeLibPtr = typeLibPtr;
            _currentTypeLibStruct = StructHelper.ReadStructureSafe<TypeLibObj_VBE>(_currentTypeLibPtr);
            Reset();
        }

        public void Dispose()
        {
            // nothing to do here, we don't own anything that needs releasing
        }

        IEnumerator IEnumerable.GetEnumerator() => this;
        public IEnumerator<TypeLibWrapper_VBE> GetEnumerator() => this;

        public IntPtr GetCurrentReference()
        {
            Marshal.AddRef(_currentTypeLibPtr);
            return _currentTypeLibPtr;
        }

        TypeLibWrapper_VBE IEnumerator<TypeLibWrapper_VBE>.Current => new TypeLibWrapper_VBE(GetCurrentReference());
        object IEnumerator.Current => new TypeLibWrapper_VBE(GetCurrentReference());

        public void Reset()  // walk back to the first project in the chain
        {
            while (_currentTypeLibStruct.Prev != IntPtr.Zero)
            {
                _currentTypeLibPtr = _currentTypeLibStruct.Prev;
                _currentTypeLibStruct = StructHelper.ReadStructureSafe<TypeLibObj_VBE>(_currentTypeLibPtr);
            }
            _isStart = true;
        }

        public bool MoveNext()
        {
            if (_isStart)
            {
                _isStart = false;  // MoveNext is called before accessing the very first item
                return true;
            }

            if (_currentTypeLibStruct.Next == IntPtr.Zero) return false;

            _currentTypeLibPtr = _currentTypeLibStruct.Next;
            _currentTypeLibStruct = StructHelper.ReadStructureSafe<TypeLibObj_VBE>(_currentTypeLibPtr);
            return true;
        }
    }

    // the main class for hooking into the live ITypeLibs provided by the VBE
    public class TypeLibsAccessor_VBE : DisposableList<TypeLibWrapper_VBE>, IDisposable
    {
        public TypeLibsAccessor_VBE(IVBE ide)
        {
            // We need at least one project in the VBE.VBProjects collection to be accessible (i.e. unprotected)
            // in order to get access to the list of loaded project TypeLibs using this method

            foreach (var project in ide.VBProjects)
            {
                using (project)
                {
                    try
                    {
                        using (var references = project.References)
                        {
                            // Now we've got the references object, we can read the internal object structure to grab the ITypeLib
                            var internalReferencesObj = StructHelper.ReadStructure<ReferencesObj_VBE>(references.Target);

                            // Now we've got this one internalReferencesObj.typeLib, we can iterate through ALL loaded project TypeLibs
                            using (var typeLibIterator = new TypeLibsIterator_VBE(internalReferencesObj.TypeLib))
                            {
                                foreach (var typeLib in typeLibIterator)
                                {
                                    Add(typeLib);
                                }
                            }
                        }

                        // we only need access to a single VBProject References object to make it work, so we can return now.
                        return;
                    }
                    finally
                    {
                        // probably a protected project, just move on to the next project.
                    }
                }
            }

            // return an empty list on error
        }

        public TypeLibWrapper_VBE FindTypeLib(string searchLibName)
        {
            foreach (var typeLib in this)
            {
                if (typeLib.Name == searchLibName)
                {
                    return typeLib;
                }
            }

            throw new ArgumentException($"TypeLibsAccessor_VBE::FindTypeLib failed. '{searchLibName}' project not found.");
        }
    }
}