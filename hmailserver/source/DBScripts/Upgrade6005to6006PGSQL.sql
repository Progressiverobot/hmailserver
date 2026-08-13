alter table hm_imapfolders add column if not exists folderspecialuse int not null default 0;

update hm_dbversion set value = 6006;
