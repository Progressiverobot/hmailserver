ALTER TABLE hm_domains ADD COLUMN domaindkimsecondaryselector nvarchar(255) NOT NULL DEFAULT '' --- [IGNORE-ERRORS]

ALTER TABLE hm_domains ADD COLUMN domaindkimsecondaryprivatekeyfile nvarchar(255) NOT NULL DEFAULT '' --- [IGNORE-ERRORS]

update hm_dbversion set value = 6007
