ALTER TABLE hm_messages add messagemodseq bigint NOT NULL DEFAULT 1

ALTER TABLE hm_imapfolders add foldercurrentmodseq bigint NOT NULL DEFAULT 1

update hm_dbversion set value = 6002
