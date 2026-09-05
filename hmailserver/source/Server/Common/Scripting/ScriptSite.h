/////////////////////////////////////////////////////////////////////////////
//
// ATL Active Script Host Wrapper
// (C) Copyright 2001 VisionTech Limited. All rights reserved.
// SPDX-License-Identifier: AGPL-3.0-or-later
// http://www.visiontech.ltd.uk/
// bateman@acm.org
//
// VisionTech Limited makes no warranties, either express or implied,
// with respect to this source code and any accompanying materials.
//
// In no event shall VisionTech Limited or its suppliers be liable for
// any damages whatsoever (including, without limitation, damages for
// loss of business profits, business interruption, loss of business
// information, or other percuniary loss) arising out of the use or
// inability to use this software.
//
// This source code may be used for any purpose, including commercial
// applications, and may be modified or redistributed subject to the
// following conditions:
//
// a) This notice may not be removed or changed in any source distribution.
//
// b) Altered source versions must be include a note to that effect,
//    and must not be misrepresented as the original.
//
// c) The origin of this software may not be misrepresented - you may
//    not claim to have written the original version. If you use this
//    source in a product, an acknowledgement in the documentation
//    would be appreciated, but is not required.
//
/////////////////////////////////////////////////////////////////////////////

#pragma once

#include <activscp.h>
#include <urlmon.h>
#include <objsafe.h>
#include <servprov.h>
#include "ScriptObjectContainer.h"
#include "ScriptObjectPolicy.h"



// If you want a different application title, declare SCRIPTSITE_APPNAME in
// you code about the reference to ScriptSite.h
#ifndef SCRIPTSITE_APPNAME
#define SCRIPTSITE_APPNAME  _T("ScriptSite")
#endif

#ifndef HR
#define HR(_ex) { HRESULT _hr = _ex; if(FAILED(_hr)) return _hr; }
#endif

/////////////////////////////////////////////////////////////////////////////
// IActiveScriptSite

class ATL_NO_VTABLE IActiveScriptSiteImpl : public IActiveScriptSite
{
private:

public:
   STDMETHOD(GetLCID)(LCID *plcid)
   {
      *plcid = LOCALE_SYSTEM_DEFAULT;
      return NOERROR;
   };

   STDMETHOD(GetItemInfo)(LPCOLESTR pstrName,DWORD dwMask,LPUNKNOWN* ppunkItem,LPTYPEINFO* ppTypeInfo)
   {
      CComPtr<IUnknown> spUnk;
      HR(LookupNamedItem(pstrName,&spUnk));

      if(dwMask & SCRIPTINFO_ITYPEINFO) {
         CComPtr<ITypeInfo> spTI;
         CComQIPtr<IProvideClassInfo> spPCI = spUnk;
         if(!!spPCI) {
            // Got IProvideClassInfo interface so use it
            HR(spPCI->GetClassInfo(&spTI));
            spPCI.Release();
         } else {
            // Try for IDispatch::GetTypeInfo
            CComQIPtr<IDispatch> spDisp = spUnk;
            if(!!spDisp) {
               HR(spDisp->GetTypeInfo(0,LOCALE_SYSTEM_DEFAULT,&spTI));
               spDisp.Release();
            }
         }
         *ppTypeInfo = spTI.Detach();

      }

      if(dwMask & SCRIPTINFO_IUNKNOWN) {
         *ppunkItem = spUnk.Detach();
      }

      return NOERROR;
   }

   STDMETHOD(GetDocVersionString)(BSTR *pbstrversionString)
   {
      if(pbstrversionString==NULL)
         return E_POINTER;
      USES_CONVERSION;
      *pbstrversionString = ::SysAllocString(T2OLE(SCRIPTSITE_APPNAME));
      return NOERROR;
   };

   // This method is called when the script engine terminates
   STDMETHOD(OnScriptTerminate)(const VARIANT* pvarResult,const EXCEPINFO* /*pexcepinfo*/)
   {
      return NOERROR;
   };

   // This method is called when the script engine's state is changed
   STDMETHOD(OnStateChange)(SCRIPTSTATE /*ssScriptState*/)
   {
      return NOERROR;
   };

   // This method is called when the script engine wants to report an error to the user
   STDMETHOD(OnScriptError)(IActiveScriptError* pase)
   {
      if(pase==NULL)
         return E_POINTER;

      // The engine allocates the three BSTRs in the EXCEPINFO for the caller, who
      // owns them from GetExceptionInfo on. They used to be dropped on every return
      // path, so every script error leaked its source, description and help-file
      // strings. Upstream f047b8c1b.
      EXCEPINFO ei = {};
      HRESULT hr = pase->GetExceptionInfo(&ei);
      if (FAILED(hr))
         return hr;

      DWORD dwContext = 0;
      ULONG ulLine = 0;
      LONG lPos = 0;
      CComBSTR src;

      if (ei.pfnDeferredFillIn != NULL)
         hr = (*ei.pfnDeferredFillIn)(&ei);

      if (SUCCEEDED(hr))
         hr = pase->GetSourcePosition(&dwContext,&ulLine,&lPos);

      if (SUCCEEDED(hr))
      {
         pase->GetSourceLineText(&src);
         hr = HandleScriptError(&ei,ulLine,lPos,src);
      }

      FreeExceptionInfoStrings(ei);
      return hr;
   }

   // Frees the strings an EXCEPINFO carries. SysFreeString accepts NULL, so this is
   // safe on a zero-initialised structure the engine never filled in.
   static void FreeExceptionInfoStrings(EXCEPINFO &ei)
   {
      ::SysFreeString(ei.bstrSource);
      ::SysFreeString(ei.bstrDescription);
      ::SysFreeString(ei.bstrHelpFile);
      ei.bstrSource = NULL;
      ei.bstrDescription = NULL;
      ei.bstrHelpFile = NULL;
   }

   // This method is called when (before) the script engine starts executing the script/event handler
   STDMETHOD(OnEnterScript)(void)
   {
      return NOERROR;
   };

   // This method is called when (after) the script engine finishes executing the script/event handler
   STDMETHOD(OnLeaveScript)(void)
   {
      return NOERROR;
   };

   // This is an implementation method.
   // Override this method in your implementation and return the desired object
   // or TYPE_E_ELEMENTNOTFOUND if the name doesn't match one of yours
   // (You must call CScriptSiteImpl::AddObject in the first place to tell
   // the script engine that your objects exist).
   STDMETHOD(LookupNamedItem)(LPCOLESTR pstrName,LPUNKNOWN* ppunkItem)
   {
      if (ppunkItem == NULL)
         return E_POINTER;

      *ppunkItem = NULL;

      // A site that is only being used to compile a script has no object container at
      // all: ScriptServer::CompileContents_ and DoesFunctionExist_ never call
      // SetObjectContainer, because they add no named items.
      if (!object_container_)
         return TYPE_E_ELEMENTNOTFOUND;

      // The return value is what says whether an object was produced. This used to
      // test "ppunkItem == 0" - the address of the caller's variable, which is never
      // null - and discard GetObjectByName's answer, so a name it had produced nothing
      // for was reported to the script engine as a successful lookup with a null item.
      if (!object_container_->GetObjectByName(pstrName, ppunkItem))
         return TYPE_E_ELEMENTNOTFOUND;

      if (*ppunkItem == NULL)
         return TYPE_E_ELEMENTNOTFOUND;

      return S_OK;
   }

   // This is an implementation method.
   // Override this method in your implementation to handle error messages
   STDMETHOD(HandleScriptError)(EXCEPINFO* pei,ULONG ulLine,LONG lPos,BSTR src)
   {
      HM::String sMsg;
      sMsg.Format(_T("Script Error: Source: %ws - Error: %08X - Description: %ws - Line: %d Column: %d - Code: %ws"),
                  pei->bstrSource,pei->scode,pei->bstrDescription,ulLine+1,lPos,src);
      
      HM::Logger::Instance()->LogError(sMsg);

      last_error_message_ = sMsg;

      return NOERROR;
   }

   HM::String GetLastError()
   {
      return last_error_message_;
   }

   protected:
      std::shared_ptr<HM::ScriptObjectContainer> object_container_;

      HM::String last_error_message_;
};

/////////////////////////////////////////////////////////////////////////////
// IActiveScriptSiteWindow

class ATL_NO_VTABLE IActiveScriptSiteWindowImpl : public IActiveScriptSiteWindow
{
public:
   // The script engine uses the window which this method returns as a
   // parent window when the engine needs to show a window (e.g. MsgBox)
   STDMETHOD(GetWindow)(HWND *phWnd)
   {
      if(phWnd==NULL)
         return E_POINTER;
      *phWnd = ::GetDesktopWindow();
      return NOERROR;
   };

   STDMETHOD(EnableModeless)(BOOL /*fEnable*/)
   {
      return NOERROR;
   };
};

/////////////////////////////////////////////////////////////////////////////
// CScriptSiteImpl

class ATL_NO_VTABLE CScriptSiteImpl : public IActiveScriptSiteImpl, public IActiveScriptSiteWindowImpl
{
public:
   CScriptSiteImpl()
   {
      wnd_ = NULL;
      init_ = false;
   }

   STDMETHOD(Initiate)(LPCTSTR pszLanguage,HWND hWnd)
   {
      if(!!engine_)
         HR(Terminate());
      wnd_ = hWnd;

      // Create new script engine
      USES_CONVERSION;
      HR(engine_.CoCreateInstance(T2COLE(pszLanguage)));

      // An engine consults its host's security manager (IInternetHostSecurityManager
      // below, answered from ScriptAllowedObjects) only when told to, through its
      // own IObjectSafety with INTERFACE_USES_SECURITY_MANAGER. Told only when the
      // list restricts something, so an unrestricted server runs its scripts by the
      // exact path it always did. An engine that cannot be told cannot be bounded,
      // and a bound the administrator asked for is not silently dropped: the
      // script does not run, and the error log says why.
      if (!HM::ScriptObjectPolicy::IsUnrestricted())
      {
         CComQIPtr<IObjectSafety> safety = engine_;
         HRESULT safetyResult = safety ? safety->SetInterfaceSafetyOptions(IID_IActiveScript,
            INTERFACE_USES_SECURITY_MANAGER, INTERFACE_USES_SECURITY_MANAGER) : E_NOINTERFACE;
         if (FAILED(safetyResult))
         {
            HM::String message;
            message.Format(_T("ScriptAllowedObjects cannot be enforced: the %s engine does not accept a host security manager (0x%08X). No script runs until the setting is * or the engine is replaced."),
                           pszLanguage, safetyResult);
            HM::Logger::Instance()->LogError(message);
            last_error_message_ = message;
            engine_.Release();
            return safetyResult;
         }
      }

      //if (lstrcmp(pszLanguage, _T("JScript")) == 0)
      //{
      //   // Set the JScript version to use - see https://docs.microsoft.com/en-us/scripting/winscript/reference/iactivescriptproperty-setproperty
      //   CComPtr<IActiveScriptProperty> pScriptProperty;
      //   HR(engine_->QueryInterface(IID_IActiveScriptProperty, reinterpret_cast<void**>(&pScriptProperty)));
      //   VARIANT engineVersion;
      //   engineVersion.vt = VT_I4;
      //   engineVersion.llVal = SCRIPTLANGUAGEVERSION_5_8;
      //   HR(pScriptProperty->SetProperty(SCRIPTPROP_INVOKEVERSIONING, 0, &engineVersion));
      //}

      // Attach to site
      HR(engine_->SetScriptSite(static_cast<IActiveScriptSite*>(this)));

      CComQIPtr<IActiveScriptParse> spParse = engine_;
      if(!spParse) return E_NOINTERFACE;
      HR(spParse->InitNew());

      init_ = true;
      return NOERROR;
   }

   STDMETHOD(Run)()
   {
      if(!init_) return E_FAIL;
      HR(engine_->SetScriptState(SCRIPTSTATE_STARTED));
      // connect - this makes the script engine handle incoming events
      HR(engine_->SetScriptState(SCRIPTSTATE_CONNECTED));
      return NOERROR;
   }

   // Returns an owning reference to the script engine. The caller's reference
   // keeps the engine object alive regardless of Terminate, which releases the
   // site's own reference. This is what allows an execution watchdog running on
   // another thread to call InterruptScriptThread without any possibility of
   // touching an engine which has already been freed.
   CComPtr<IActiveScript> GetEngine()
   {
      return engine_;
   }

   STDMETHOD(Terminate)()
   {
      if(init_)
      {
         // Disconnect the host application from the engine. This will prevent
         // the further firing of events. Event sinks that are in progress will
         // be completed before the state changes.
         engine_->SetScriptState(SCRIPTSTATE_DISCONNECTED);

         // Call to InterruptScriptThread to abandon any running scripts and
         // force cleanup of all script elements.
         engine_->InterruptScriptThread(SCRIPTTHREADID_ALL,NULL,0);

         init_ = false;
      }

      if(!!engine_) 
      {
         // Always call prior to release
         engine_->Close();
         engine_.Release();
      }

      return NOERROR;
   }

   bool 
   ProcedureExists(HM::String sName)
   {
      // Determines wether a procedure exists in
      // the script. 

      IDispatch *ScriptDispatch = NULL;

      // No engine, no procedures. Initiate() can fail to create the engine
      // (a missing or policy-blocked VBScript/JScript engine), and
      // ScriptServer::DoesFunctionExist_ reaches here regardless of whether it
      // succeeded, so this used to dereference a null CComPtr during a script
      // reload. Upstream #583.
      if (!engine_)
         return false;

      // An engine whose script was interrupted may not hand back a dispatch at
      // all, so the result is checked rather than dereferenced blind. The
      // reference is owned by this function and must be released either way.
      HRESULT dispatchResult = engine_->GetScriptDispatch(NULL, &ScriptDispatch);

      if (FAILED(dispatchResult) || ScriptDispatch == NULL)
         return false;

      DISPID dispid;
      BSTR names[1];
      names[0] = sName.AllocSysString();
      HRESULT hr = ScriptDispatch->GetIDsOfNames( IID_NULL, names, 1, 0, &dispid );

      SysFreeString(names[0]);
      ScriptDispatch->Release();

      if ( SUCCEEDED( hr ) )
         return true;
      else
         return false;
   }

   STDMETHOD(AddScript)(LPCTSTR pszScript,LPCTSTR pszContext=NULL)
   {
      if(!init_) return E_FAIL;

      CComQIPtr<IActiveScriptParse> spParse = engine_;
      if(!spParse) return E_NOINTERFACE;

      USES_CONVERSION;
      const DWORD dwFlags = SCRIPTTEXT_ISVISIBLE;

      // A parse failure fills the EXCEPINFO with strings the caller owns. They were
      // never freed, so every failed Reload or Check-syntax leaked them. Upstream
      // f047b8c1b.
      EXCEPINFO einfo = {};
      HRESULT hr = spParse->ParseScriptText(T2COLE(pszScript),pszContext!=NULL ? T2COLE(pszContext) : OLESTR(""),NULL,NULL,0,0,dwFlags,NULL,&einfo);

      IActiveScriptSiteImpl::FreeExceptionInfoStrings(einfo);

      return hr;
   }

   STDMETHOD(SetObjectContainer)(std::shared_ptr<HM::ScriptObjectContainer> pObject)
   {
      object_container_ = pObject;

      // Add the objects to namespace
      std::vector<HM::String> vecNames = object_container_->GetObjectNames();
      
      for(HM::String name : vecNames)
      {
         AddObject(name, TRUE);
      }

      return S_OK;
   }
      
   STDMETHOD(AddObject)(LPCTSTR pszName,BOOL bGlobalCollection=FALSE)
   {
      if(!init_) return E_FAIL;

      DWORD dwFlags = SCRIPTITEM_ISVISIBLE;
      if(bGlobalCollection)
         dwFlags |= SCRIPTITEM_GLOBALMEMBERS;

      USES_CONVERSION;
      return engine_->AddNamedItem(T2COLE(pszName),dwFlags);
   }

   STDMETHOD(GetWindow)(HWND *phWnd)
   {
      if(phWnd==NULL)
         return E_POINTER;
      *phWnd = wnd_;
      return NOERROR;
   };

protected:
   ~CScriptSiteImpl()
   {
      if(!!engine_)
         Terminate();
   }

protected:
   HWND                    wnd_;
   bool                    init_;
   CComPtr<IActiveScript>  engine_;
};

/////////////////////////////////////////////////////////////////////////////
// IInternetHostSecurityManager
//
// The script engines ask their host before CreateObject (VBScript) or new
// ActiveXObject (JScript) instantiates a class: ProcessUrlAction with
// URLACTION_ACTIVEX_RUN and the CLSID, and then QueryCustomPolicy with
// GUID_CUSTOM_CONFIRMOBJECTSAFETY for the object they made. A site without this
// interface lets them create anything, which is what every hMailServer did until
// now; with it, the answer comes from ScriptAllowedObjects. A denied class fails
// inside the script with the engine's own "can't create object" error (429),
// which the script can trap like any other failure, and one application-log line
// names the class and the setting.

class ATL_NO_VTABLE IInternetHostSecurityManagerImpl : public IInternetHostSecurityManager
{
public:
   STDMETHOD(GetSecurityId)(BYTE * /*pbSecurityId*/, DWORD * /*pcbSecurityId*/, DWORD_PTR /*dwReserved*/)
   {
      return E_NOTIMPL;
   }

   STDMETHOD(ProcessUrlAction)(DWORD dwAction, BYTE *pPolicy, DWORD cbPolicy, BYTE *pContext, DWORD cbContext, DWORD /*dwFlags*/, DWORD /*dwReserved*/)
   {
      if (pPolicy == NULL || cbPolicy < sizeof(DWORD))
         return E_INVALIDARG;

      DWORD policy = URLPOLICY_ALLOW;
      if (dwAction == URLACTION_ACTIVEX_RUN)
      {
         CLSID clsid = {0};
         if (pContext != NULL && cbContext >= sizeof(CLSID))
            memcpy(&clsid, pContext, sizeof(CLSID));

         if (!ClassAllowed_(clsid))
            policy = URLPOLICY_DISALLOW;
      }

      *reinterpret_cast<DWORD*>(pPolicy) = policy;
      HM::String what;
      what.Format(_T("url action 0x%04X: %s"), (unsigned) dwAction, policy == URLPOLICY_ALLOW ? _T("allowed") : _T("refused"));
      Trace_(what, cbContext);
      return policy == URLPOLICY_ALLOW ? S_OK : S_FALSE;
   }

   STDMETHOD(QueryCustomPolicy)(REFGUID guidKey, BYTE **ppPolicy, DWORD *pcbPolicy, BYTE *pContext, DWORD cbContext, DWORD /*dwReserved*/)
   {
      if (ppPolicy == NULL || pcbPolicy == NULL)
         return E_POINTER;
      *ppPolicy = NULL;
      *pcbPolicy = 0;

      // GUID_CUSTOM_CONFIRMOBJECTSAFETY, objsafe.h: "may the script use the object it
      // has just created". Answered here for the same reason ProcessUrlAction is:
      // left to the engine's default, it would refuse every class not marked safe
      // for scripting - Scripting.FileSystemObject among them - whatever the list
      // says. Any other question is the engine's.
      // {10200490-fa38-11d0-ac0e-00a0c90fffc0}: urlmon.h declares the constant and no
      // import library the SDK ships defines it, so the value is written here - read
      // back from what the engine asks, since the value in circulation was wrong.
      static const GUID ConfirmObjectSafety = { 0x10200490, 0xfa38, 0x11d0, { 0xac, 0x0e, 0x00, 0xa0, 0xc9, 0x0f, 0xff, 0xc0 } };
      OLECHAR asked[64] = {0};
      StringFromGUID2(guidKey, asked, sizeof(asked) / sizeof(asked[0]));
      if (!IsEqualGUID(guidKey, ConfirmObjectSafety))
      {
         Trace_(HM::String(_T("custom policy ")) + asked + _T(" left to the engine"), cbContext);
         return INET_E_DEFAULT_ACTION;
      }

      // The context is objsafe.h's CONFIRMSAFETY - the class, the object and flags -
      // and the class is its first field. Only the class is read and only the
      // class's size is required: the engine passes the structure with a size
      // short of the padded sizeof, and a strict check denied every listed class.
      DWORD policy = URLPOLICY_DISALLOW;
      if (pContext != NULL && cbContext >= sizeof(CLSID))
      {
         CLSID clsid = {0};
         memcpy(&clsid, pContext, sizeof(CLSID));
         if (ClassAllowed_(clsid))
            policy = URLPOLICY_ALLOW;
      }

      *ppPolicy = static_cast<BYTE*>(CoTaskMemAlloc(sizeof(DWORD)));
      if (*ppPolicy == NULL)
         return E_OUTOFMEMORY;
      *reinterpret_cast<DWORD*>(*ppPolicy) = policy;
      *pcbPolicy = sizeof(DWORD);
      Trace_(policy == URLPOLICY_ALLOW ? _T("confirm object safety: allowed") : _T("confirm object safety: refused"), cbContext);
      return S_OK;
   }

private:
   // What the engine asked and what it was told, in the debug log.
   static void Trace_(const HM::String &what, DWORD contextBytes)
   {
      if (!(HM::Logger::Instance()->GetLogMask() & HM::Logger::LSDebug))
         return;
      HM::String message;
      message.Format(_T("Script host: %s (%u context bytes)."), what.c_str(), (unsigned) contextBytes);
      HM::Logger::Instance()->LogDebug(message);
   }

   static bool ClassAllowed_(REFCLSID clsid)
   {
      HM::String objectName;
      if (HM::ScriptObjectPolicy::IsAllowed(clsid, objectName))
         return true;

      HM::String message;
      message.Format(_T("Script denied: CreateObject of %s is not in ScriptAllowedObjects."), objectName.c_str());
      if (HM::Logger::Instance()->GetLogMask() & HM::Logger::LSApplication)
         HM::Logger::Instance()->LogApplication(message);
      return false;
   }
};

/////////////////////////////////////////////////////////////////////////////
// CScriptSiteBasic
//
// This is the minimum code needed to run a script engine. It has no support
// for your own object model, and displays errors as a messagebox. To
// overcome this, you'll need to write your own version and override
// LookupNamedItem and/or HandleScriptError.
//
// Use it like this:
//
//   LPCTSTR strScriptCode; // this points to the script code we want to run
//   ...
//   CComObject<CScriptSiteBasic>* pBasic;
//   CComQIPtr<IActiveScriptSite> spUnk;
//   HR(CComObject<CScriptSiteBasic>::CreateInstance(&pBasic));
//   spUnk = pBasic; // let CComQIPtr tidy up for us
//   HR(pBasic->Initiate(_T("jscript"),GetDesktopWindow()));
//   HR(pBasic->AddScript(strScriptCode));
//   HR(pBasic->Run());
//   HR(pBasic->Terminate());

class ATL_NO_VTABLE CScriptSiteBasic :
   public CComObjectRootEx<CComSingleThreadModel>,
   public CScriptSiteImpl,
   public IInternetHostSecurityManagerImpl,
   public IServiceProvider
{
public:
   DECLARE_PROTECT_FINAL_CONSTRUCT()
   BEGIN_COM_MAP(CScriptSiteBasic)
      COM_INTERFACE_ENTRY(IActiveScriptSite)
      COM_INTERFACE_ENTRY(IActiveScriptSiteWindow)
      COM_INTERFACE_ENTRY(IInternetHostSecurityManager)
      COM_INTERFACE_ENTRY(IServiceProvider)
   END_COM_MAP()

   // An engine may ask for the security manager as a service
   // (SID_SInternetHostSecurityManager, whose value is the interface's IID) rather
   // than as an interface on the site; both roads lead to the same object.
   STDMETHOD(QueryService)(REFGUID guidService, REFIID riid, void **ppv)
   {
      if (ppv == NULL)
         return E_POINTER;
      *ppv = NULL;

      if (IsEqualGUID(guidService, __uuidof(IInternetHostSecurityManager)))
         return GetUnknown()->QueryInterface(riid, ppv);

      return E_NOINTERFACE;
   }
};



