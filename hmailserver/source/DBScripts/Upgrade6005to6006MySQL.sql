alter table hm_imapfolders add folderspecialuse int not null default 0 /* [IGNORE-ERRORS] */;

update hm_dbversion set value = 6006;
