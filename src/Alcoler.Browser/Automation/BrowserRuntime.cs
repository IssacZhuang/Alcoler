namespace Alcoler.Browser.Automation;

internal static class BrowserRuntime
{
    internal const string Install = """
        (() => {
          if (globalThis.__alcolerAutomation) return true;
          const visible = e => {
            if (!(e instanceof Element) || !e.isConnected) return false;
            const s = getComputedStyle(e), r = e.getBoundingClientRect();
            return s.display !== 'none' && s.visibility !== 'hidden' && s.visibility !== 'collapse' && r.width > 0 && r.height > 0;
          };
          const enabled = e => !e.matches(':disabled') && e.getAttribute('aria-disabled') !== 'true' && !e.closest('[inert]');
          const text = e => (e.innerText ?? e.textContent ?? '').replace(/\s+/g, ' ').trim();
          const role = e => e.getAttribute('role') || ({A:e.hasAttribute('href')?'link':'generic',BUTTON:'button',SELECT:e.multiple?'listbox':'combobox',TEXTAREA:'textbox',IMG:'img',H1:'heading',H2:'heading',H3:'heading',H4:'heading',H5:'heading',H6:'heading',LI:'listitem',UL:'list',OL:'list',TABLE:'table',TR:'row',TD:'cell',TH:'columnheader',OPTION:'option',DIALOG:'dialog',NAV:'navigation',MAIN:'main'}[e.tagName]) || (e.tagName==='INPUT' ? ({checkbox:'checkbox',radio:'radio',range:'slider',button:'button',submit:'button',reset:'button'}[e.type] || (e.type==='hidden'?'none':'textbox')) : 'generic');
          const name = e => {
            const labelled = e.getAttribute('aria-labelledby');
            if (labelled) return labelled.split(/\s+/).map(id => e.ownerDocument.getElementById(id)?.textContent || '').join(' ').trim();
            return e.getAttribute('aria-label') || (e.labels?.length ? Array.from(e.labels).map(text).join(' ') : '') || e.getAttribute('alt') || (['button','submit','reset'].includes(e.type) ? e.value : '') || e.getAttribute('title') || (['button','link','heading','option','checkbox','radio'].includes(role(e)) ? text(e) : '') || '';
          };
          const roots = root => {
            const result = [root];
            for (let i=0;i<result.length;i++) for (const e of result[i].querySelectorAll('*')) if (e.shadowRoot) result.push(e.shadowRoot);
            return result;
          };
          const all = root => roots(root).flatMap(r => Array.from(r.querySelectorAll('*')));
          const matches = (actual, expected, exact) => {
            if (expected && typeof expected==='object' && expected.regex !== undefined) return new RegExp(expected.regex, expected.flags || '').test(actual);
            return exact ? actual === String(expected) : actual.toLowerCase().includes(String(expected).toLowerCase());
          };
          const resolve = (spec, root=document) => {
            if (typeof spec === 'string') {
              if (spec.startsWith('ref=')) return resolve({ref:spec.slice(4)},root);
              if (spec.startsWith('text=')) return resolve({text:spec.slice(5)},root);
              if (spec.startsWith('role=')) return resolve({role:spec.slice(5)},root);
              spec = {css:spec};
            }
            spec ||= {};
            if (spec.selector !== undefined) spec = typeof spec.selector==='string' ? {...spec,css:spec.selector,selector:undefined} : {...spec,...spec.selector,selector:undefined};
            if (spec.ref) {
              const e = api.refs.get(String(spec.ref));
              if (!e || !e.isConnected) throw new Error('Stale element reference; take a new snapshot.');
              return [e];
            }
            if (spec.frameLocator || spec.frame) {
              const f = typeof (spec.frameLocator || spec.frame)==='string' ? (spec.frameLocator || spec.frame) : (spec.frameLocator || spec.frame).selector;
              const owners = resolve(f,root);
              if (owners.length!==1) throw new Error('Frame locator must match exactly one frame.');
              if (!owners[0].contentDocument) throw new Error('Frame requires a CDP execution context.');
              root = owners[0].contentDocument;
            }
            let result;
            if (spec.css) result = roots(root).flatMap(r => Array.from(r.querySelectorAll(spec.css)));
            else result = all(root);
            if (spec.role) result = result.filter(e => role(e)===spec.role);
            if (spec.name!==undefined) result = result.filter(e => matches(name(e),spec.name,spec.exact ?? true));
            if (spec.text!==undefined) result = result.filter(e => matches(text(e),spec.text,spec.exact ?? false) && !Array.from(e.children).some(c => matches(text(c),spec.text,spec.exact ?? false)));
            if (spec.label!==undefined) result = result.filter(e => e.labels && Array.from(e.labels).some(l => matches(text(l),spec.label,spec.exact ?? true)) || e.hasAttribute('aria-label') && matches(e.getAttribute('aria-label'),spec.label,spec.exact ?? true));
            if (spec.placeholder!==undefined) result = result.filter(e => e.hasAttribute('placeholder') && matches(e.getAttribute('placeholder'),spec.placeholder,spec.exact ?? true));
            if (spec.testId!==undefined || spec.test_id!==undefined) result = result.filter(e => e.getAttribute(spec.testIdAttribute || 'data-testid')===String(spec.testId ?? spec.test_id));
            if (spec.hasText!==undefined) result=result.filter(e=>matches(text(e),spec.hasText,false));
            if (spec.and) for(const item of (Array.isArray(spec.and)?spec.and:[spec.and])) { const intersection = new Set(resolve(item,root)); result = result.filter(e => intersection.has(e)); }
            if (spec.or) {
              const alternatives=(Array.isArray(spec.or)?spec.or:[spec.or]).flatMap(item=>resolve(item,root));
              const hasBase=['css','role','name','text','label','placeholder','testId','test_id'].some(k=>spec[k]!==undefined);
              result = [...new Set([...(hasBase?result:[]),...alternatives])];
            }
            if (spec.filter) {
              const f=spec.filter;
              if (f.hasText!==undefined || f.has_text!==undefined) result=result.filter(e => matches(text(e),f.hasText ?? f.has_text,false));
              if (f.hasNotText!==undefined || f.has_not_text!==undefined) result=result.filter(e => !matches(text(e),f.hasNotText ?? f.has_not_text,false));
              if (f.visible!==undefined) result=result.filter(e => visible(e)===f.visible);
              if (f.has) result=result.filter(e => resolve(f.has,e).length>0);
              if (f.hasNot || f.has_not) result=result.filter(e => resolve(f.hasNot || f.has_not,e).length===0);
            }
            if (spec.visible!==undefined) result=result.filter(e => visible(e)===spec.visible);
            if (spec.nth!==undefined) { const i=Number(spec.nth); result = result.slice(i<0?result.length+i:i,(i<0?result.length+i:i)+1); }
            return [...new Set(result)];
          };
          const select = spec => { const es=resolve(spec); if(es.length!==1) throw new Error(`Strict locator matched ${es.length} elements; use nth or a more specific locator.`); return es[0]; };
          const inspect = function() {
            const e=this, r=e.getBoundingClientRect(), s=getComputedStyle(e);
            let x=Math.max(0,Math.min(innerWidth,r.right))-Math.max(0,r.left), y=Math.max(0,Math.min(innerHeight,r.bottom))-Math.max(0,r.top);
            x=Math.max(0,r.left)+x/2; y=Math.max(0,r.top)+y/2;
            let hit=e.ownerDocument.elementFromPoint(x,y);
            while(hit?.shadowRoot) { const next=hit.shadowRoot.elementFromPoint(x,y); if(!next || next===hit) break; hit=next; }
            return {connected:e.isConnected,visible:visible(e),enabled:enabled(e),editable:!e.readOnly && (e.isContentEditable || e instanceof HTMLInputElement || e instanceof HTMLTextAreaElement),hit:hit===e || e.contains(hit),x,y,width:r.width,height:r.height,left:r.left,top:r.top,tag:e.tagName.toLowerCase(),role:role(e),name:name(e),text:text(e).slice(0,10000),value:e.value ?? null,checked:e.checked ?? null,attributes:Object.fromEntries(Array.from(e.attributes).map(a=>[a.name,a.value])),display:s.display};
          };
          const api={resolve,select,visible,enabled,text,role,name,inspect,refs:new Map(),snapshot:(generation,limit=1000,root=document,includeHidden=false)=>{
            const elements=[...(root instanceof Element?[root]:[]),...all(root)];
            api.refs.clear(); let i=0;
            return elements.filter(e=>includeHidden || visible(e)).filter(e=>role(e)!=='generic' || e.matches('input,textarea,[contenteditable],[tabindex]')).slice(0,limit).map(e=>{
              const ref=`d${generation}_${++i}`; api.refs.set(ref,e);
              const r=e.getBoundingClientRect();
              return {ref,role:role(e),name:name(e),text:text(e).slice(0,300),tag:e.tagName.toLowerCase(),disabled:!enabled(e),checked:e.checked,expanded:e.getAttribute('aria-expanded'),value:e.value,bounds:{x:r.x,y:r.y,width:r.width,height:r.height}};
            });
          }};
          Object.defineProperty(globalThis,'__alcolerAutomation',{value:api,configurable:true});
          return true;
        })()
        """;
}
