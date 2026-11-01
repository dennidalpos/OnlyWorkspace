const fs = require('node:fs'), vm = require('node:vm'), assert = require('node:assert/strict');
const html = fs.readFileSync(require('node:path').join(__dirname, '../../src/OnlyWorkspace.Outlook/UI/TaskPane.html'), 'utf8');
function node(tag, cls, text) { return {tag, cls, textContent:text, children:[], disabled:false, hidden:false, events:{},
 appendChild(n){this.children.push(n);return n;}, insertBefore(n){this.children.unshift(n);},setAttribute(){},
 addEventListener(k,f){this.events[k]=f;},get firstChild(){return this.children[0];},get lastChild(){return this.children[this.children.length-1];}}; }
const messages=[], buttons=[];
const context=vm.createContext({Map,Date,state:{busy:false},el:node,icon:()=>'',announce(){},post:m=>messages.push(m),
 document:{createElement:t=>node(t),createTextNode:t=>node('text',null,t)},
 actionButton:(i,label,click)=>{const n=node('button');n.appendChild(node('text',null,label));n.click=()=>{if(!n.disabled)click();};buttons.push(n);return n;}});
vm.runInContext(html.slice(html.indexOf('  var actionItemEntries ='),html.indexOf('  function dataTable(')),context);
const item=i=>({title:'Voce '+i,kind:'task',selectable:true,saveResultId:'result',saveItemId:String(i)});
context.addActionItems(node('div'),[item(0),item(1)]);
function reply(m,outcomes){context.actionItemsSaved({requestId:m.requestId,resultId:m.resultId,outcomes});}
const query=messages.at(-1);assert.equal(query.type,'get_action_item_states');
reply(query,[{itemId:'0',saved:false},{itemId:'1',saved:false}]);
const first=buttons[1],second=buttons[3];
first.click();const saving=messages.at(-1);assert.equal(first.lastChild.textContent,'Salvataggio…');
const count=messages.length;first.click();assert.equal(messages.length,count,'no duplicate click');
reply({...saving,requestId:'stale'},[{itemId:'0',saved:true}]);assert.equal(first.lastChild.textContent,'Salvataggio…');
reply(saving,[{itemId:'0',saved:true}]);assert.equal(first.lastChild.textContent,'Aggiunta');assert.ok(first.disabled);
second.click();reply(messages.at(-1),[{itemId:'1',saved:false,error:'Non salvata'}]);assert.equal(second.disabled,false);
second.click();reply(messages.at(-1),[{itemId:'1',saved:true}]);assert.equal(second.lastChild.textContent,'Aggiunta');
context.addActionItems(node('div'),[item(0),item(1)]);
const reload=messages.at(-1);reply(reload,[{itemId:'0',saved:true},{itemId:'1',saved:true}]);
assert.ok(buttons[5].disabled && buttons[7].disabled,'reload keeps successful items disabled');
reply(saving,[{itemId:'0',saved:false,error:'late'}]);assert.ok(buttons[5].disabled,'late reply cannot change rerendered result');
context.conv = {children: []}; context.$ = () => ({hidden:false}); context.egress = {blocked:false};
vm.runInContext(html.slice(html.indexOf('  function clearConversation() {'),html.indexOf('  function removeStaleRetryButtons()')),context);
context.clearConversation();assert.equal(context.actionItemEntries.size,0,'new chat releases old result handlers');
reply(reload,[{itemId:'0',saved:false,error:'late after clear'}]);assert.equal(context.actionItemEntries.size,0);
console.log('PASS action-item saves: host acknowledgement, errors, retry, duplicate clicks, stale replies and reload');
