alter table hm_messages add messagemodseq bigint not null default 1

alter table hm_imapfolders add foldercurrentmodseq bigint not null default 1

update hm_dbversion set value = 6002
