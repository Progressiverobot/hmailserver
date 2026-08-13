ALTER TABLE hm_imapfolders ADD folderspecialuse int NOT NULL DEFAULT 0 --- [IGNORE-ERRORS]

update hm_dbversion set value = 6006
